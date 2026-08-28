<#
.SYNOPSIS
    Consume the retained win-x64 FFmpeg runtime by exact digest (#236, W1-A4).

.DESCRIPTION
    The W2 entry point. It takes NO package reference, NO digest, NO tag and NO
    run id, and that absence is the security property rather than an ergonomic
    shortcoming: the identity of what W2 builds against travels with the commit
    W2 is building, in `accepted-runtime.json`, and a caller who could pass a
    different reference could substitute a different runtime.

    A `-Reference` parameter is deliberately NOT offered. Neither is an
    environment-variable fallback. If this script is ever given one, that is a
    regression, and `negative-controls.py` asserts it stays absent.

    Order of operations, and it matters:

      1. read and validate the committed acceptance manifest;
      2. pull the MANIFEST alone and verify its bytes against the committed
         digest BEFORE any blob is fetched;
      3. verify the config and layer descriptors against the committed manifest;
      4. pull the layer, verify its digest before opening it;
      5. verify every contained path and digest, refusing missing, added,
         renamed, duplicated and case-colliding content;
      6. extract, refusing traversal and absolute paths on the way out as well
         as on the way in;
      7. verify the runtime archive and the corresponding-source stream;
      8. expose the runtime read-only.

    Nothing is extracted before step 5 completes. A consumer that extracts and
    then checks has already written the attacker's bytes to disk.

.PARAMETER AcceptedManifest
    Path to the committed `accepted-runtime.json`. This names a file in the
    repository being built, not a package.

.PARAMETER WorkDir
    Scratch directory for the pulled blobs.

.PARAMETER OutDir
    Where the verified runtime is exposed.

.PARAMETER OrasPath
    Path to the pinned ORAS client installed by `install-oras.sh`.

    There is deliberately no registry override, not even for tests. An override
    is a caller-supplied redirect to another registry, which is the same defect
    as a caller-supplied reference wearing a different name. The registry-side
    controls exercise `oci-protocol.sh` instead, which is the publisher's tool.
#>
[CmdletBinding(DefaultParameterSetName = 'Consume')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Consume')][string] $AcceptedManifest,
    [Parameter(Mandatory = $true, ParameterSetName = 'Consume')][string] $WorkDir,
    [Parameter(Mandatory = $true, ParameterSetName = 'Consume')][string] $OutDir,
    [Parameter(Mandatory = $true, ParameterSetName = 'Consume')][string] $OrasPath,
    # A pure grammar oracle for `reference-corpus.py`, in its own parameter set.
    # It is NOT a caller-selectable identity: it reaches no registry, opens no
    # manifest and returns before ORAS is ever consulted, and the corpus asserts
    # that. It exists because the alternative — a second copy of this grammar
    # written for the test — would prove the copy and not the consumer.
    [Parameter(Mandatory = $true, ParameterSetName = 'GrammarCheck')]
    [AllowEmptyString()][string] $GrammarCheck,
    # Also apply the canonical-package authority, which the grammar deliberately
    # does not carry: `localhost:5000/...` is well formed and the local-registry
    # controls need it, while only THIS consumer insists on our one package.
    [Parameter(ParameterSetName = 'GrammarCheck')][switch] $Canonical
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Stop-Consumption {
    param([string] $Message)
    throw "W1-A4 CONSUME HARD STOP: $Message"
}

# The one authorised package. A constant, not a parameter — the same statement
# `contract.py` makes on the Python side, repeated here because this script is
# what actually talks to the registry.
$CanonicalPackage = 'ghcr.io/tesserafin-project/windows-ffmpeg-runtime'

function Get-Sha256 {
    param([string] $Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

# The reference grammar, restated for the third and last time.
#
# R0 found this parser using `[^@]+` for the repository path — the same
# permissive shape `oci-protocol.sh` had already been repaired for. It accepted
# a reference carrying BOTH a tag and a digest, and, because PowerShell's
# `-notmatch` is CASE-INSENSITIVE, it also accepted an uppercase digest that the
# other two parsers refuse. Both are fixed here: the classification order, the
# reason tokens and the messages are identical to `contract.classify_reference`
# and `classify_reference` in `oci-protocol.sh`, and the match is `-cnotmatch`
# so a canonical lowercase digest means what it says.
$ReferenceHost = '[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:[0-9]{1,5})?'
$ReferenceComponent = '[a-z0-9]+([._-][a-z0-9]+)*'

$ReferenceReasonText = @{
    'tag-only'         = 'is not digest-pinned; a tag is never an accepted identity'
    'missing-digest'   = 'is not digest-pinned; it names no sha256 digest'
    'multiple-at'      = "carries more than one '@'"
    'tag-and-digest'   = 'carries both a tag and a digest; use the digest alone'
    'malformed-digest' = 'does not end in a canonical lowercase sha256:<64 hex> digest'
    'malformed-name'   = 'has a malformed registry or repository'
}

function Get-ReferenceRejectionReason {
    param([string] $Reference)
    # Set-StrictMode makes a pipeline that yields one object have no .Count, so
    # the separators are counted by length difference instead.
    $ats = $Reference.Length - $Reference.Replace('@', '').Length
    if ($ats -eq 0) {
        $last = $Reference.Split('/')[-1]
        if ($last.Contains(':')) { return 'tag-only' }
        return 'missing-digest'
    }
    if ($ats -gt 1) { return 'multiple-at' }
    $at = $Reference.IndexOf('@')
    $name = $Reference.Substring(0, $at)
    $digest = $Reference.Substring($at + 1)
    if ($digest -cnotmatch '^sha256:[0-9a-f]{64}$') { return 'malformed-digest' }
    if ($name.Split('/')[-1].Contains(':')) { return 'tag-and-digest' }
    if ($name -cnotmatch "^$ReferenceHost(/$ReferenceComponent)+`$") { return 'malformed-name' }
    return $null
}

function Assert-DigestReference {
    param([string] $Reference)
    $reason = Get-ReferenceRejectionReason $Reference
    if ($null -ne $reason) {
        Stop-Consumption "REFERENCE-REJECTED:$reason '$Reference' $($ReferenceReasonText[$reason])"
    }
    $name = $Reference.Split('@')[0]
    if ($name -cne $CanonicalPackage) {
        Stop-Consumption "REFERENCE-REJECTED:not-canonical-package '$name' is not the authorised package; W1-A4 authorises exactly one: $CanonicalPackage"
    }
}

if ($PSCmdlet.ParameterSetName -eq 'GrammarCheck') {
    if ($Canonical) {
        try { Assert-DigestReference $GrammarCheck }
        catch { Write-Error $_.Exception.Message -ErrorAction Continue; exit 1 }
        Write-Output "the consumer accepts '$GrammarCheck'"
        exit 0
    }
    $reason = Get-ReferenceRejectionReason $GrammarCheck
    if ($null -ne $reason) {
        Write-Error "REFERENCE-REJECTED:$reason '$GrammarCheck' $($ReferenceReasonText[$reason])" -ErrorAction Continue
        exit 1
    }
    Write-Output "the grammar accepts '$GrammarCheck'"
    exit 0
}

function Assert-SafeRelativePath {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { Stop-Consumption 'empty path in the retained layer' }
    if ($Path.StartsWith('/') -or $Path.StartsWith('\')) {
        Stop-Consumption "absolute path in the retained layer: '$Path'"
    }
    if ($Path -match '^[A-Za-z]:') {
        Stop-Consumption "drive-qualified path in the retained layer: '$Path'"
    }
    if ($Path.Contains('\')) {
        Stop-Consumption "backslash in the retained layer path '$Path'"
    }
    foreach ($segment in $Path.Split('/')) {
        if ($segment -eq '' -or $segment -eq '.' -or $segment -eq '..') {
            Stop-Consumption "traversal or empty segment in the retained layer path: '$Path'"
        }
    }
}

# ── 1. the committed acceptance manifest ────────────────────────────────────
if (-not (Test-Path -LiteralPath $AcceptedManifest)) {
    Stop-Consumption "no acceptance manifest at '$AcceptedManifest'"
}
$accepted = Get-Content -LiteralPath $AcceptedManifest -Raw | ConvertFrom-Json

foreach ($field in @(
        'schemaVersion', 'platform', 'reference', 'manifestDigest', 'manifestSize',
        'configDigest', 'configSize', 'layerDigest', 'layerSize', 'runtimePath',
        'runtimeSha256', 'correspondingSourcePath', 'correspondingSourceSha256',
        'correspondingSourceStreamSha256', 'unitPaths', 'licence')) {
    if (-not $accepted.PSObject.Properties.Name.Contains($field)) {
        Stop-Consumption "the acceptance manifest has no '$field' field"
    }
}
if ($accepted.schemaVersion -ne 1) {
    Stop-Consumption "acceptance manifest schemaVersion $($accepted.schemaVersion) is not implemented by this consumer"
}
if ($accepted.platform -ne 'win-x64') {
    Stop-Consumption "acceptance manifest platform '$($accepted.platform)' is not win-x64"
}

$reference = $accepted.reference
Assert-DigestReference $reference

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$plain = @()

# ── 2. the manifest, verified before a single blob is fetched ───────────────
$manifestPath = Join-Path $WorkDir 'manifest.json'
& $OrasPath manifest fetch @plain --output $manifestPath $reference
if ($LASTEXITCODE -ne 0) { Stop-Consumption "could not fetch the manifest for $reference" }

$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
$actualManifestDigest = 'sha256:' + (Get-Sha256 $manifestPath)
if ($actualManifestDigest -ne $accepted.manifestDigest) {
    Stop-Consumption "the registry returned manifest $actualManifestDigest, but the committed identity is $($accepted.manifestDigest)"
}
if ($manifestBytes.Length -ne $accepted.manifestSize) {
    Stop-Consumption "the returned manifest is $($manifestBytes.Length) bytes, but the committed identity records $($accepted.manifestSize)"
}

# ── 3. descriptors, from what came back rather than from what we trust ──────
$manifest = [System.Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
if ($manifest.artifactType -ne $accepted.artifactType) {
    Stop-Consumption "artifactType '$($manifest.artifactType)' is not the accepted '$($accepted.artifactType)'"
}
if ($manifest.config.digest -ne $accepted.configDigest -or $manifest.config.size -ne $accepted.configSize) {
    Stop-Consumption 'the returned manifest names a config this consumer did not accept'
}
if ($manifest.layers.Count -ne 1) {
    Stop-Consumption "the returned manifest carries $($manifest.layers.Count) layers; the retention unit is exactly one"
}
if ($manifest.layers[0].digest -ne $accepted.layerDigest -or $manifest.layers[0].size -ne $accepted.layerSize) {
    Stop-Consumption 'the returned manifest names a layer this consumer did not accept'
}

# ── 4. the layer, verified before it is opened ──────────────────────────────
$layerPath = Join-Path $WorkDir 'layer.tar'
& $OrasPath blob fetch @plain --output $layerPath "$($reference.Split('@')[0])@$($accepted.layerDigest)"
if ($LASTEXITCODE -ne 0) { Stop-Consumption 'could not fetch the retained layer' }

$actualLayerDigest = 'sha256:' + (Get-Sha256 $layerPath)
if ($actualLayerDigest -ne $accepted.layerDigest) {
    Stop-Consumption "the retained layer hashes to $actualLayerDigest, not the accepted $($accepted.layerDigest)"
}
if ((Get-Item -LiteralPath $layerPath).Length -ne $accepted.layerSize) {
    Stop-Consumption 'the retained layer is not the accepted size'
}

# ── 5. every path and every digest, before anything is extracted ────────────
$pinned = @{}
foreach ($property in $accepted.unitPaths.PSObject.Properties) {
    Assert-SafeRelativePath $property.Name
    $pinned[$property.Name] = $property.Value
}

$extractRoot = Join-Path $WorkDir 'unit'
if (Test-Path -LiteralPath $extractRoot) { Remove-Item -Recurse -Force -LiteralPath $extractRoot }
New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null

# tar is present on every supported Windows runner image. The entry names are
# read and checked BEFORE extraction; `--list` writes nothing to disk.
$entries = & tar --list --file $layerPath
if ($LASTEXITCODE -ne 0) { Stop-Consumption 'the retained layer is not a readable tar' }

$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$seenLower = @{}
foreach ($entry in $entries) {
    $name = $entry.TrimEnd("`r")
    if ($name.EndsWith('/')) { Stop-Consumption "directory entry in the retained layer: '$name'" }
    Assert-SafeRelativePath $name
    if (-not $seen.Add($name)) {
        Stop-Consumption "duplicate entry in the retained layer: '$name'"
    }
    $lowered = $name.ToLowerInvariant()
    if ($seenLower.ContainsKey($lowered)) {
        Stop-Consumption "'$name' and '$($seenLower[$lowered])' differ only by case and cannot both be extracted on Windows"
    }
    $seenLower[$lowered] = $name
    if (-not $pinned.ContainsKey($name)) {
        Stop-Consumption "the retained layer carries '$name', which the acceptance manifest does not pin"
    }
}
foreach ($name in $pinned.Keys) {
    if (-not $seen.Contains($name)) {
        Stop-Consumption "the acceptance manifest pins '$name', which the retained layer does not carry"
    }
}

# ── 6. extraction, into a directory that holds nothing else ─────────────────
& tar --extract --file $layerPath --directory $extractRoot
if ($LASTEXITCODE -ne 0) { Stop-Consumption 'the retained layer could not be extracted' }

foreach ($name in $pinned.Keys) {
    $path = Join-Path $extractRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Stop-Consumption "'$name' did not extract as a file"
    }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        Stop-Consumption "'$name' extracted as a reparse point, not a file"
    }
    $digest = Get-Sha256 $path
    if ($digest -ne $pinned[$name].sha256) {
        Stop-Consumption "'$name' hashes to $digest, but the acceptance manifest pins $($pinned[$name].sha256)"
    }
    if ($item.Length -ne $pinned[$name].size) {
        Stop-Consumption "'$name' is $($item.Length) bytes, but the acceptance manifest pins $($pinned[$name].size)"
    }
}

$extracted = Get-ChildItem -Recurse -File -LiteralPath $extractRoot
if ($extracted.Count -ne $pinned.Count) {
    Stop-Consumption "extraction produced $($extracted.Count) files, but the acceptance manifest pins $($pinned.Count)"
}

# ── 7. the two halves of the GPL obligation, verified as bytes ──────────────
$runtimeArchive = Join-Path $extractRoot $accepted.runtimePath
$sourceArchive = Join-Path $extractRoot $accepted.correspondingSourcePath

if (-not (Test-Path -LiteralPath $runtimeArchive -PathType Leaf)) {
    Stop-Consumption "the retained unit carries no runtime at '$($accepted.runtimePath)'"
}
if (-not (Test-Path -LiteralPath $sourceArchive -PathType Leaf)) {
    Stop-Consumption ("GPL-3.0-or-later refusal: the retained unit carries a runtime binary with no " +
        "corresponding source at '$($accepted.correspondingSourcePath)'. Consuming the binary without " +
        'its source is not a permitted state at any point.')
}

$runtimeDigest = Get-Sha256 $runtimeArchive
if ($runtimeDigest -ne $accepted.runtimeSha256) {
    Stop-Consumption "the retained runtime hashes to $runtimeDigest, not the accepted $($accepted.runtimeSha256)"
}
$sourceDigest = Get-Sha256 $sourceArchive
if ($sourceDigest -ne $accepted.correspondingSourceSha256) {
    Stop-Consumption "the retained source hashes to $sourceDigest, not the accepted $($accepted.correspondingSourceSha256)"
}

# The DECOMPRESSED stream, not just the container. A .tar.zst whose container
# matches while its stream does not is exactly the drift a container hash
# cannot see, and it is the difference between shipping the corresponding
# source and shipping something shaped like it.
$streamDigestFile = Join-Path $WorkDir 'source-stream.sha256'
$zstd = Get-Command zstd -ErrorAction SilentlyContinue
if ($null -eq $zstd) {
    Stop-Consumption 'zstd is not available; the corresponding-source stream cannot be verified, and an unverified source is not an accepted one'
}
& zstd -dc $sourceArchive > (Join-Path $WorkDir 'source.tar')
if ($LASTEXITCODE -ne 0) { Stop-Consumption 'the corresponding source could not be decompressed' }
$streamDigest = Get-Sha256 (Join-Path $WorkDir 'source.tar')
Set-Content -LiteralPath $streamDigestFile -Value $streamDigest -NoNewline
if ($streamDigest -ne $accepted.correspondingSourceStreamSha256) {
    Stop-Consumption "the corresponding-source stream hashes to $streamDigest, not the accepted $($accepted.correspondingSourceStreamSha256)"
}
Remove-Item -LiteralPath (Join-Path $WorkDir 'source.tar') -Force

# ── 8. expose the runtime, unmodified ───────────────────────────────────────
if (Test-Path -LiteralPath $OutDir) { Remove-Item -Recurse -Force -LiteralPath $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Copy-Item -LiteralPath $runtimeArchive -Destination (Join-Path $OutDir (Split-Path -Leaf $accepted.runtimePath))
Copy-Item -LiteralPath $sourceArchive -Destination (Join-Path $OutDir (Split-Path -Leaf $accepted.correspondingSourcePath))
Get-ChildItem -File -LiteralPath $OutDir | ForEach-Object { $_.IsReadOnly = $true }

$evidence = [ordered]@{
    reference                        = $reference
    manifestDigest                   = $accepted.manifestDigest
    layerDigest                      = $accepted.layerDigest
    verifiedPaths                    = $pinned.Count
    runtimeSha256                    = $runtimeDigest
    correspondingSourceSha256        = $sourceDigest
    correspondingSourceStreamSha256  = $streamDigest
    tagsAccepted                     = $false
    callerSuppliedReference          = $false
    extractedBeforeVerification      = $false
}
$evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $WorkDir 'consume.json') -Encoding utf8

Write-Host "consumed $reference"
Write-Host "  verified $($pinned.Count) retained paths"
Write-Host "  runtime  $runtimeDigest"
Write-Host "  source   $sourceDigest (stream $streamDigest)"
