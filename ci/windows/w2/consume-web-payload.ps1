#Requires -Version 7.2
<#
.SYNOPSIS
    Acquire and verify the accepted Tesserafin Web payload on Windows, by digest,
    with no Docker daemon and no container runtime of any kind.

.DESCRIPTION
    W2-A0 (#256). The Windows server distribution needs the same web bundle the
    Linux packages ship. On Linux, ci/package/assemble-payload.sh gets it with
    `docker pull` + `docker cp`. A hosted `windows-latest` runner has no Linux
    container runtime, so that route does not exist, and shelling out to Docker
    Desktop is not a supply chain anyone should accept for a release artifact.

    This script therefore speaks the OCI distribution protocol directly: it
    exchanges a job-scoped token, fetches the manifest, verifies the manifest
    bytes against the requested digest BEFORE parsing them, fetches the config
    and every layer, verifies each descriptor's bytes against both its declared
    digest and its declared size, verifies each decompressed layer against the
    config's `rootfs.diff_ids`, applies the layers in manifest order through a
    tar reader that rejects everything a tar entry can do that a payload has no
    business doing, and only then hands over an output directory.

    Nothing here trusts the registry. The chain is: caller supplies an immutable
    digest -> manifest bytes hash to that digest -> descriptors come from those
    verified bytes -> blob bytes hash to those descriptors -> extracted tree
    hashes to the pinned canonical `pkg_tree_digest` -> the revision recorded
    inside the payload matches the pinned web commit. A break anywhere leaves no
    output at all.

    The handoff is a single directory rename out of a private staging directory
    that is a sibling of the destination, so a half-verified tree is never
    visible under the accepted path, not even briefly.

.PARAMETER Fixture
    Permits a non-accepted registry/repository/reference, and only then. It is
    additionally gated on the registry being a loopback address, so it can drive
    the disposable local registries in web-payload-controls.py and cannot be
    used to point the real contract at somewhere else.

.NOTES
    The bearer token never appears in a command line, a file name, an
    environment dump, generated evidence or an error message. It is read from an
    environment variable, held in memory, and every string this script emits goes
    through Protect-Secret first.
#>

[CmdletBinding()]
param(
    [string] $Registry = 'ghcr.io',
    [string] $Repository = 'tesserafin-project/tesserafin-web-assets',
    [string] $Reference = 'sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c',
    [string] $ExpectedTreeDigest = '4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f',
    [string] $ExpectedRevision = 'a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a',
    [string] $PayloadRoot = 'web',
    [string] $RevisionPath = 'metadata/web-revision.json',

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $EvidencePath,
    [string] $TokenEnvironmentVariable = 'GHCR_TOKEN',
    [ValidateSet('https', 'http')]
    [string] $Scheme = 'https',
    [string] $PythonPath,
    [switch] $Fixture
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---------------------------------------------------------------------------
# The accepted contract. These five values are the whole of W2-A0's identity;
# ci/windows/w2/web-payload-controls.py asserts them against the ruling, so a
# typo here is a failing control rather than a silently different payload.
# ---------------------------------------------------------------------------
$AcceptedRegistry     = 'ghcr.io'
$AcceptedRepository   = 'tesserafin-project/tesserafin-web-assets'
$AcceptedReference    = 'sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c'
$AcceptedTreeDigest   = '4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f'
$AcceptedRevision     = 'a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a'

# Media types this consumer will act on. Anything else is refused rather than
# guessed at, including image indexes: the accepted descriptor is a plain image
# manifest, and a consumer that silently learns to pick a platform is a consumer
# whose output is no longer decided by the pinned digest alone.
$ManifestMediaTypes = @(
    'application/vnd.oci.image.manifest.v1+json',
    'application/vnd.docker.distribution.manifest.v2+json'
)
$IndexMediaTypes = @(
    'application/vnd.oci.image.index.v1+json',
    'application/vnd.docker.distribution.manifest.list.v2+json'
)
$ConfigMediaTypes = @(
    'application/vnd.oci.image.config.v1+json',
    'application/vnd.docker.container.image.v1+json'
)
$GzipLayerMediaTypes = @(
    'application/vnd.oci.image.layer.v1.tar+gzip',
    'application/vnd.docker.image.rootfs.diff.tar.gzip'
)
$PlainLayerMediaTypes = @(
    'application/vnd.oci.image.layer.v1.tar'
)

# A manifest is a small JSON document. Refusing to buffer more than this keeps a
# hostile registry from being able to make the client allocate before a single
# byte has been authenticated.
$MaxManifestBytes = 4MB
$MaxConfigBytes = 16MB

# Windows reserved device names, with or without an extension. These are not
# creatable, and a payload that contains one is a payload that unpacks
# differently on Windows than it does anywhere else.
$ReservedNames = '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$'

$Secrets = [System.Collections.Generic.List[string]]::new()
$Denials = [System.Collections.Generic.List[object]]::new()

function Protect-Secret {
    param([string] $Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    foreach ($secret in $Secrets) {
        if (-not [string]::IsNullOrEmpty($secret)) {
            $Text = $Text.Replace($secret, '<redacted>')
        }
    }
    return $Text
}

function Deny {
    param([string] $Property, [string] $Message)
    $clean = Protect-Secret $Message
    $Denials.Add([pscustomobject]@{ property = $Property; message = $clean })
    throw ("W2-A0 DENY [{0}] {1}" -f $Property, $clean)
}

function Write-Note {
    param([string] $Message)
    Write-Host ("W2-A0: {0}" -f (Protect-Secret $Message))
}

function Test-Sha256Digest {
    param([string] $Value)
    return ($Value -cmatch '^sha256:[0-9a-f]{64}$')
}

function Get-FileSha256 {
    param([string] $Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { return 'sha256:' + [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
}

# ---------------------------------------------------------------------------
# Preflight: everything this script needs must exist before it touches the
# network, so a missing prerequisite is never mistaken for a bad payload.
# ---------------------------------------------------------------------------
function Assert-Prerequisite {
    try { $null = [System.Formats.Tar.TarReader] }
    catch {
        Deny 'prerequisite' ("System.Formats.Tar is unavailable on this host (PowerShell {0}, .NET {1}); W2-A0 does not fall back to an external archiver" -f
            $PSVersionTable.PSVersion, [System.Environment]::Version)
    }
}

function Resolve-Python {
    if ($PythonPath) {
        if (-not (Test-Path -LiteralPath $PythonPath)) { Deny 'prerequisite' "no python at $PythonPath" }
        return $PythonPath
    }
    foreach ($candidate in @('python3', 'python')) {
        foreach ($found in @(Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue)) {
            # On Windows, `python3` is frequently an App Execution Alias that
            # opens a store page instead of running anything. A consumer whose
            # digest step silently resolves to that is a consumer that fails for
            # a reason nobody can read, so it is skipped rather than tried.
            if ($found.Source -like '*\WindowsApps\*') { continue }
            return $found.Source
        }
    }
    Deny 'prerequisite' 'no python3/python interpreter on PATH; the canonical tree digest cannot be computed'
}

# ---------------------------------------------------------------------------
# Gates 1 and 2: an immutable reference, aimed at the accepted contract.
# ---------------------------------------------------------------------------
function Assert-Reference {
    if (-not (Test-Sha256Digest $Reference)) {
        Deny 'immutable-reference' ("'{0}' is not an immutable sha256 digest; W2-A0 never resolves a tag" -f $Reference)
    }
    if (-not ($ExpectedTreeDigest -cmatch '^[0-9a-f]{64}$')) {
        Deny 'immutable-reference' "the expected tree digest is not a full lowercase sha256"
    }
    if (-not ($ExpectedRevision -cmatch '^[0-9a-f]{40}$')) {
        Deny 'immutable-reference' "the expected web revision is not a full lowercase 40-character commit"
    }
}

function Assert-Contract {
    $isAccepted = ($Registry -ceq $AcceptedRegistry) -and
                  ($Repository -ceq $AcceptedRepository) -and
                  ($Reference -ceq $AcceptedReference) -and
                  ($ExpectedTreeDigest -ceq $AcceptedTreeDigest) -and
                  ($ExpectedRevision -ceq $AcceptedRevision) -and
                  ($Scheme -ceq 'https')
    if ($isAccepted) {
        if ($Fixture) { Deny 'accepted-contract' 'the accepted contract must not be consumed in fixture mode' }
        return $true
    }
    if (-not $Fixture) {
        Deny 'accepted-contract' ("{0}://{1}/{2}@{3} is not the accepted W2-A0 contract" -f
            $Scheme, $Registry, $Repository, $Reference)
    }
    # Fixture mode exists to drive a disposable local registry, and nothing else.
    $host_ = ($Registry -split ':')[0]
    if ($host_ -notin @('127.0.0.1', 'localhost', '::1', '[::1]')) {
        Deny 'accepted-contract' ("fixture mode is restricted to a loopback registry, got '{0}'" -f $host_)
    }
    return $false
}

# ---------------------------------------------------------------------------
# Authentication. The token is read from the environment, never from a
# parameter: a parameter is visible in the process table to every other process
# on the machine, and W2-A0's whole authentication boundary is that it isn't.
# ---------------------------------------------------------------------------
function Get-RegistryBearer {
    $raw = [System.Environment]::GetEnvironmentVariable($TokenEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Deny 'authentication' ("no registry credential in `${0}; W2-A0 does not fall back to an anonymous pull" -f $TokenEnvironmentVariable)
    }
    $Secrets.Add($raw)

    # The registry's own basic credential. Added to the scrub list before it is
    # ever used, because a base64 of the token is exactly as disclosing as the
    # token, and Actions does not mask it.
    $basic = [System.Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("x-access-token:$raw"))
    $Secrets.Add($basic)

    $uri = ('{0}://{1}/token?service={1}&scope=repository:{2}:pull' -f $Scheme, $Registry, $Repository)
    try {
        $response = Invoke-RestMethod -Uri $uri -Method Get -Headers @{ Authorization = "Basic $basic" } `
            -MaximumRedirection 3 -ErrorAction Stop
    } catch {
        Deny 'authentication' ("token exchange with {0}://{1} failed: {2}" -f $Scheme, $Registry, $_.Exception.Message)
    }
    $bearer = $null
    foreach ($field in @('token', 'access_token')) {
        if ($response.PSObject.Properties.Name -contains $field -and $response.$field) {
            $bearer = [string]$response.$field
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($bearer)) {
        Deny 'authentication' 'the token endpoint returned no bearer token'
    }
    $Secrets.Add($bearer)
    return $bearer
}

function Invoke-RegistryDownload {
    param(
        [string] $Bearer,
        [string] $Path,
        [string] $Destination,
        [string[]] $Accept
    )
    $uri = ('{0}://{1}/v2/{2}/{3}' -f $Scheme, $Registry, $Repository, $Path)
    $headers = @{ Authorization = "Bearer $Bearer" }
    if ($Accept) { $headers['Accept'] = ($Accept -join ', ') }
    try {
        Invoke-WebRequest -Uri $uri -Method Get -Headers $headers -OutFile $Destination `
            -MaximumRedirection 5 -ErrorAction Stop | Out-Null
    } catch {
        Deny 'registry' ("GET /v2/{0}/{1} failed: {2}" -f $Repository, $Path, $_.Exception.Message)
    }
}

# ---------------------------------------------------------------------------
# Gate 8/9: what a tar entry is allowed to be called. Everything here is a
# refusal, never a repair: a payload whose names have to be rewritten to be safe
# is not the payload anybody accepted.
# ---------------------------------------------------------------------------
function Resolve-SafeEntryName {
    param([string] $Name)

    if ([string]::IsNullOrEmpty($Name)) { Deny 'path-safety' 'an entry with an empty name' }

    $normalised = $Name
    while ($normalised.StartsWith('./')) { $normalised = $normalised.Substring(2) }
    $normalised = $normalised.TrimEnd('/')
    if ($normalised -eq '' -or $normalised -eq '.') { return '' }   # the archive root itself

    if ($normalised.Contains('\')) {
        Deny 'path-safety' ("entry '{0}' contains a backslash" -f $Name)
    }
    foreach ($char in $normalised.ToCharArray()) {
        if ([int]$char -lt 32 -or [int]$char -eq 127) {
            Deny 'path-safety' ("entry '{0}' contains a control character" -f $Name)
        }
        if ('<>:"|?*'.Contains($char)) {
            # ':' is both the drive separator and NTFS alternate-data-stream
            # syntax; the rest are simply not creatable on Windows.
            Deny 'path-safety' ("entry '{0}' contains the reserved character '{1}'" -f $Name, $char)
        }
    }
    if ($normalised.StartsWith('/')) {
        Deny 'path-safety' ("entry '{0}' is an absolute path" -f $Name)
    }
    if ($normalised.Length -gt 220) {
        Deny 'path-safety' ("entry '{0}' exceeds the 220-character payload path budget" -f $Name)
    }

    $parts = $normalised -split '/'
    foreach ($part in $parts) {
        if ($part -eq '') { Deny 'path-safety' ("entry '{0}' has an empty path component" -f $Name) }
        if ($part -eq '.' -or $part -eq '..') {
            Deny 'path-safety' ("entry '{0}' traverses with '{1}'" -f $Name, $part)
        }
        if ($part -match $ReservedNames) {
            Deny 'path-safety' ("entry '{0}' uses the reserved device name '{1}'" -f $Name, $part)
        }
        if ($part -ne $part.TrimEnd(' ', '.')) {
            Deny 'path-safety' ("entry '{0}' has a component ending in a dot or space" -f $Name)
        }
    }
    return ($parts -join '/')
}

function Resolve-TargetPath {
    param([string] $Root, [string] $Relative)
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($Root, ($Relative -replace '/', [System.IO.Path]::DirectorySeparatorChar)))
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not $rootFull.EndsWith([string][System.IO.Path]::DirectorySeparatorChar)) {
        $rootFull += [System.IO.Path]::DirectorySeparatorChar
    }
    if (-not $full.StartsWith($rootFull, [System.StringComparison]::Ordinal)) {
        Deny 'path-safety' ("entry '{0}' resolves outside the extraction root" -f $Relative)
    }
    return $full
}

# ---------------------------------------------------------------------------
# Layer application. One layer at a time, in manifest order, into one
# accumulating root, with overlay whiteouts honoured for the form that has an
# unambiguous meaning and refused for the form that does not.
# ---------------------------------------------------------------------------
function Add-Layer {
    param(
        [string] $TarPath,
        [string] $Root,
        [System.Collections.Generic.Dictionary[string, string]] $CaseIndex,
        [int] $Index
    )
    $applied = 0
    $whiteouts = 0
    $stream = [System.IO.File]::OpenRead($TarPath)
    try {
        $reader = [System.Formats.Tar.TarReader]::new($stream, $false)
        try {
            while ($true) {
                $entry = $reader.GetNextEntry($false)
                if ($null -eq $entry) { break }

                $kind = $entry.EntryType.ToString()
                if ($kind -notin @('RegularFile', 'V7RegularFile', 'Directory')) {
                    Deny 'entry-type' ("layer {0} entry '{1}' is a {2}; W2-A0 accepts only regular files and directories" -f
                        $Index, $entry.Name, $kind)
                }

                $relative = Resolve-SafeEntryName $entry.Name
                if ($relative -eq '') { continue }

                $parts = $relative -split '/'
                $leaf = $parts[-1]

                if ($leaf.StartsWith('.wh.')) {
                    if ($leaf -eq '.wh..wh..opq' -or $leaf.StartsWith('.wh..wh.')) {
                        Deny 'whiteout' ("layer {0} uses the opaque/special whiteout '{1}'; W2-A0 refuses rather than guess at its scope" -f
                            $Index, $entry.Name)
                    }
                    $targetName = $leaf.Substring(4)
                    if ($targetName -eq '' -or $targetName -eq '.' -or $targetName -eq '..') {
                        Deny 'whiteout' ("layer {0} has the malformed whiteout '{1}'" -f $Index, $entry.Name)
                    }
                    $targetRelative = if ($parts.Count -gt 1) {
                        (($parts[0..($parts.Count - 2)]) -join '/') + '/' + $targetName
                    } else { $targetName }
                    $targetRelative = Resolve-SafeEntryName $targetRelative
                    $targetPath = Resolve-TargetPath $Root $targetRelative
                    if (Test-Path -LiteralPath $targetPath) {
                        Remove-Item -LiteralPath $targetPath -Recurse -Force
                    }
                    $prefix = $targetRelative.ToLowerInvariant()
                    foreach ($key in @($CaseIndex.Keys)) {
                        if ($key -eq $prefix -or $key.StartsWith($prefix + '/')) { $null = $CaseIndex.Remove($key) }
                    }
                    $whiteouts++
                    continue
                }

                # NTFS folds case. Two entries that differ only in case would
                # silently become one file whose content depends on layer order,
                # and the tree digest would move for a reason nothing reports.
                $key = $relative.ToLowerInvariant()
                if ($CaseIndex.ContainsKey($key) -and $CaseIndex[$key] -cne $relative) {
                    Deny 'case-collision' ("layer {0} entry '{1}' collides case-insensitively with '{2}'" -f
                        $Index, $relative, $CaseIndex[$key])
                }
                $CaseIndex[$key] = $relative

                $target = Resolve-TargetPath $Root $relative
                if ($entry.EntryType.ToString() -eq 'Directory') {
                    $null = [System.IO.Directory]::CreateDirectory($target)
                } else {
                    $parent = [System.IO.Path]::GetDirectoryName($target)
                    $null = [System.IO.Directory]::CreateDirectory($parent)
                    if ([System.IO.File]::Exists($target)) { [System.IO.File]::Delete($target) }
                    elseif ([System.IO.Directory]::Exists($target)) {
                        Deny 'entry-type' ("layer {0} replaces the directory '{1}' with a file" -f $Index, $relative)
                    }
                    $out = [System.IO.File]::Create($target)
                    try {
                        if ($null -ne $entry.DataStream) { $entry.DataStream.CopyTo($out) }
                    } finally { $out.Dispose() }
                }
                $applied++
            }
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    return [pscustomobject]@{ entries = $applied; whiteouts = $whiteouts }
}

function Expand-Gzip {
    param([string] $Source, [string] $Destination)
    $input_ = [System.IO.File]::OpenRead($Source)
    try {
        $gzip = [System.IO.Compression.GZipStream]::new($input_, [System.IO.Compression.CompressionMode]::Decompress)
        try {
            $out = [System.IO.File]::Create($Destination)
            try { $gzip.CopyTo($out) } finally { $out.Dispose() }
        } finally { $gzip.Dispose() }
    } finally { $input_.Dispose() }
}

# ---------------------------------------------------------------------------

$staging = $null
$accepted = $false
try {
    Assert-Prerequisite
    Assert-Reference
    $isAcceptedContract = Assert-Contract
    $python = Resolve-Python

    $OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $OutputPath) {
        Deny 'atomic-handoff' ("the output path '{0}' already exists; W2-A0 never merges into an existing tree" -f $OutputPath)
    }
    $outputParent = [System.IO.Path]::GetDirectoryName($OutputPath)
    if ([string]::IsNullOrEmpty($outputParent)) { Deny 'atomic-handoff' 'the output path has no parent directory' }
    $null = [System.IO.Directory]::CreateDirectory($outputParent)

    # A sibling of the destination, so the final handoff is a rename on one
    # volume and cannot degrade into a copy that is observable half-done.
    $staging = [System.IO.Path]::Combine($outputParent, ('.w2a0-' + [System.Guid]::NewGuid().ToString('N')))
    $null = [System.IO.Directory]::CreateDirectory($staging)
    $blobs = [System.IO.Path]::Combine($staging, 'blobs')
    $rootfs = [System.IO.Path]::Combine($staging, 'rootfs')
    $null = [System.IO.Directory]::CreateDirectory($blobs)
    $null = [System.IO.Directory]::CreateDirectory($rootfs)

    Write-Note ("consuming {0}://{1}/{2}@{3}" -f $Scheme, $Registry, $Repository, $Reference)
    if (-not $isAcceptedContract) { Write-Note 'fixture mode: a disposable loopback registry, not the accepted contract' }

    $bearer = Get-RegistryBearer

    # --- the manifest, authenticated before it is parsed --------------------
    $manifestPath = [System.IO.Path]::Combine($blobs, 'manifest.bin')
    Invoke-RegistryDownload -Bearer $bearer -Path ("manifests/{0}" -f $Reference) -Destination $manifestPath `
        -Accept ($ManifestMediaTypes + $IndexMediaTypes)
    $manifestSize = (Get-Item -LiteralPath $manifestPath).Length
    if ($manifestSize -gt $MaxManifestBytes) {
        Deny 'manifest-digest' ("the manifest is {0} bytes, over the {1}-byte ceiling" -f $manifestSize, $MaxManifestBytes)
    }
    $manifestDigest = Get-FileSha256 $manifestPath
    if ($manifestDigest -cne $Reference) {
        Deny 'manifest-digest' ("the registry served {0} for {1}" -f $manifestDigest, $Reference)
    }
    Write-Note ("manifest {0} verified ({1} bytes)" -f $manifestDigest, $manifestSize)

    $manifest = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($manifestPath)) |
        ConvertFrom-Json
    if ($manifest.PSObject.Properties.Name -notcontains 'mediaType') {
        Deny 'media-type' 'the manifest declares no mediaType'
    }
    if ($manifest.mediaType -in $IndexMediaTypes) {
        Deny 'media-type' ("{0} is an image index; the accepted W2-A0 descriptor is a single-platform image manifest" -f $manifest.mediaType)
    }
    if ($manifest.mediaType -notin $ManifestMediaTypes) {
        Deny 'media-type' ("unsupported manifest media type '{0}'" -f $manifest.mediaType)
    }

    # --- the config, and the diff_ids it commits to ------------------------
    if (-not (Test-Sha256Digest $manifest.config.digest)) {
        Deny 'descriptor' ("the config descriptor digest '{0}' is malformed" -f $manifest.config.digest)
    }
    if ($manifest.config.mediaType -notin $ConfigMediaTypes) {
        Deny 'media-type' ("unsupported config media type '{0}'" -f $manifest.config.mediaType)
    }
    if ([int64]$manifest.config.size -gt $MaxConfigBytes) {
        Deny 'descriptor' ("the config descriptor claims {0} bytes, over the ceiling" -f $manifest.config.size)
    }
    $configPath = [System.IO.Path]::Combine($blobs, 'config.bin')
    Invoke-RegistryDownload -Bearer $bearer -Path ("blobs/{0}" -f $manifest.config.digest) -Destination $configPath
    $configSize = (Get-Item -LiteralPath $configPath).Length
    if ($configSize -ne [int64]$manifest.config.size) {
        Deny 'descriptor-size' ("the config is {0} bytes, the descriptor declares {1}" -f $configSize, $manifest.config.size)
    }
    $configDigest = Get-FileSha256 $configPath
    if ($configDigest -cne $manifest.config.digest) {
        Deny 'descriptor-digest' ("the config hashes to {0}, the descriptor declares {1}" -f $configDigest, $manifest.config.digest)
    }
    $config = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($configPath)) | ConvertFrom-Json
    if ($config.rootfs.type -cne 'layers') {
        Deny 'descriptor' ("the config declares rootfs.type '{0}'" -f $config.rootfs.type)
    }
    $diffIds = @($config.rootfs.diff_ids)
    $layers = @($manifest.layers)
    if ($layers.Count -lt 1) { Deny 'descriptor' 'the manifest declares no layers' }
    if ($diffIds.Count -ne $layers.Count) {
        Deny 'descriptor' ("the config commits to {0} diff_ids for {1} layers" -f $diffIds.Count, $layers.Count)
    }

    # --- the layers, in manifest order -------------------------------------
    $caseIndex = [System.Collections.Generic.Dictionary[string, string]]::new()
    $inventory = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $layers.Count; $i++) {
        $descriptor = $layers[$i]
        if (-not (Test-Sha256Digest $descriptor.digest)) {
            Deny 'descriptor' ("layer {0} descriptor digest '{1}' is malformed" -f $i, $descriptor.digest)
        }
        $gzipped = $descriptor.mediaType -in $GzipLayerMediaTypes
        if (-not $gzipped -and $descriptor.mediaType -notin $PlainLayerMediaTypes) {
            Deny 'media-type' ("layer {0} has the unsupported media type '{1}'" -f $i, $descriptor.mediaType)
        }
        $blobPath = [System.IO.Path]::Combine($blobs, ("layer{0}.bin" -f $i))
        Invoke-RegistryDownload -Bearer $bearer -Path ("blobs/{0}" -f $descriptor.digest) -Destination $blobPath
        $blobSize = (Get-Item -LiteralPath $blobPath).Length
        if ($blobSize -ne [int64]$descriptor.size) {
            Deny 'descriptor-size' ("layer {0} is {1} bytes, the descriptor declares {2}" -f $i, $blobSize, $descriptor.size)
        }
        $blobDigest = Get-FileSha256 $blobPath
        if ($blobDigest -cne $descriptor.digest) {
            Deny 'descriptor-digest' ("layer {0} hashes to {1}, the descriptor declares {2}" -f $i, $blobDigest, $descriptor.digest)
        }

        $tarPath = [System.IO.Path]::Combine($blobs, ("layer{0}.tar" -f $i))
        if ($gzipped) {
            try { Expand-Gzip -Source $blobPath -Destination $tarPath }
            catch { Deny 'layer-decompression' ("layer {0} is not readable as gzip: {1}" -f $i, $_.Exception.Message) }
        } else {
            [System.IO.File]::Copy($blobPath, $tarPath)
        }
        $diffId = Get-FileSha256 $tarPath
        if ($diffId -cne $diffIds[$i]) {
            Deny 'diff-id' ("layer {0} decompresses to {1}, the config commits to {2}" -f $i, $diffId, $diffIds[$i])
        }

        $result = Add-Layer -TarPath $tarPath -Root $rootfs -CaseIndex $caseIndex -Index $i
        [System.IO.File]::Delete($blobPath)
        [System.IO.File]::Delete($tarPath)
        $inventory.Add([pscustomobject]@{
            index = $i
            mediaType = $descriptor.mediaType
            digest = $descriptor.digest
            size = [int64]$descriptor.size
            diffId = $diffId
            entries = $result.entries
            whiteouts = $result.whiteouts
        })
        Write-Note ("layer {0} verified and applied: {1}, {2} bytes, {3} entries, {4} whiteouts" -f
            $i, $descriptor.digest, $descriptor.size, $result.entries, $result.whiteouts)
    }

    # --- the payload's own provenance --------------------------------------
    $revisionRelative = Resolve-SafeEntryName $RevisionPath
    $revisionFile = Resolve-TargetPath $rootfs $revisionRelative
    if (-not [System.IO.File]::Exists($revisionFile)) {
        Deny 'web-revision' ("the payload has no '{0}'" -f $RevisionPath)
    }
    $revisionDocument = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($revisionFile)) | ConvertFrom-Json
    $revision = [string]$revisionDocument.revision
    if ($revision -cne $ExpectedRevision) {
        Deny 'web-revision' ("the payload records web revision '{0}', the pin requires '{1}'" -f $revision, $ExpectedRevision)
    }
    $epoch = [int64]$revisionDocument.sourceDateEpoch
    if ($epoch -le 0) { Deny 'web-revision' 'the payload records no usable sourceDateEpoch' }

    $payloadRelative = Resolve-SafeEntryName $PayloadRoot
    $payloadDir = Resolve-TargetPath $rootfs $payloadRelative
    if (-not [System.IO.Directory]::Exists($payloadDir)) {
        Deny 'payload-root' ("the payload has no '{0}' directory" -f $PayloadRoot)
    }

    # --- the canonical tree digest -----------------------------------------
    $digestScript = [System.IO.Path]::Combine($PSScriptRoot, 'pkg-tree-digest.py')
    if (-not [System.IO.File]::Exists($digestScript)) { Deny 'prerequisite' 'pkg-tree-digest.py is missing' }
    $treeDigest = (& $python $digestScript $payloadDir $epoch 2>&1 | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0) {
        Deny 'tree-digest' ("the canonical digest could not be computed: {0}" -f $treeDigest)
    }
    $treeDigest = ([string]$treeDigest).Trim()
    if ($treeDigest -cne $ExpectedTreeDigest) {
        Deny 'tree-digest' ("the extracted payload hashes to {0}, the pin requires {1}" -f $treeDigest, $ExpectedTreeDigest)
    }
    Write-Note ("canonical pkg_tree_digest {0} at epoch {1}" -f $treeDigest, $epoch)
    Write-Note ("web revision {0}" -f $revision)

    # --- the handoff: one rename, or nothing -------------------------------
    [System.IO.Directory]::Move($payloadDir, $OutputPath)
    $accepted = $true
    Write-Note ("accepted payload at {0}" -f $OutputPath)

    if ($EvidencePath) {
        $evidenceParent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($EvidencePath))
        $null = [System.IO.Directory]::CreateDirectory($evidenceParent)
        $evidence = [ordered]@{
            contract = if ($isAcceptedContract) { 'accepted' } else { 'fixture' }
            registry = $Registry
            repository = $Repository
            reference = $Reference
            manifestMediaType = $manifest.mediaType
            manifestSize = $manifestSize
            configMediaType = $manifest.config.mediaType
            configDigest = $manifest.config.digest
            configSize = [int64]$manifest.config.size
            layers = @($inventory)
            payloadRoot = $PayloadRoot
            webRevision = $revision
            sourceDateEpoch = $epoch
            treeDigest = $treeDigest
            outputPath = $OutputPath
            containerRuntime = 'none'
            actionsArtifactHandoff = $false
        }
        $json = ($evidence | ConvertTo-Json -Depth 6)
        [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($EvidencePath), (Protect-Secret $json))
    }
    exit 0
} catch {
    $message = Protect-Secret $_.Exception.Message
    if (-not $message.StartsWith('W2-A0 DENY')) { $message = "W2-A0 DENY [unexpected] $message" }
    [Console]::Error.WriteLine($message)
    exit 1
} finally {
    # Whether this succeeded or failed, nothing survives except the accepted
    # output. A failure that leaves a half-extracted tree behind is a failure
    # that a later step can mistake for a payload.
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $accepted -and $OutputPath -and (Test-Path -LiteralPath $OutputPath)) {
        Remove-Item -LiteralPath $OutputPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}
