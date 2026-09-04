#Requires -Version 7.2
<#
.SYNOPSIS
    Assemble the portable, self-contained win-x64 Tesserafin server ZIP, and
    refuse to produce one at all if anything about it is not the accepted thing.

.DESCRIPTION
    W2-A2 (#256). `docs/distribution/W0-windows-server.md` §6 defines the portable
    ZIP as one top-level directory holding the self-contained server, the bundled
    Web payload, the Tesserafin FFmpeg runtime, licences, SBOM and the provenance
    manifest, carrying no state, with fixed modes and a clamped mtime derived from
    SOURCE_DATE_EPOCH, ordered deterministically, "so two clean builds produce
    identical bytes".

    This script is the packer for exactly that. Everything it puts in the archive
    it either acquires through a FROZEN consumer it does not author, or produces
    from the commit being built. Nothing is acquired from a tag, from "the
    latest", from an Actions artifact or from a container daemon.

      * the Web payload  -> ci/windows/w2/consume-web-payload.ps1        (W2-A0)
      * the FFmpeg runtime -> ci/windows/runtime-retention/consume.ps1   (W1/W2-A1)
        driven by ci/windows/runtime-retention/accepted-runtime.json
      * the server       -> dotnet publish, self-contained, win-x64

    Both consumers are inputs to this script and never outputs of it. There is
    deliberately no wrapper around either: a wrapper is where a `-Reference`, a
    `-Tag` or a `-RunId` eventually gets added, and the whole security property
    of the frozen consumers is that the identity of what W2 builds against
    travels with the commit. `ci/windows/w2/zip-controls.py` asserts that this
    script grows no such parameter.

    SOURCE_DATE_EPOCH is a required INPUT. It is never read from the clock, never
    derived from a tag, and never defaulted: an archive whose timestamps came
    from when it happened to be built is not reproducible, and one that silently
    substituted "now" for a missing epoch would look reproducible for exactly as
    long as nobody built it twice.

    Fail-closed, in the order the checks can first be made:

      * SOURCE_DATE_EPOCH missing, zero, negative, or outside what a ZIP can
        represent (the MS-DOS date field starts at 1980-01-01);
      * the extracted Web tree does not hash to the accepted WEB_PAYLOAD_SHA256;
      * the inner FFmpeg archive does not hash to the accepted runtimeSha256;
      * the publish tree is not a self-contained win-x64 one -- the host
        components are absent, the runtimeconfig declares a shared framework, or
        tesserafin.exe is not a PE x64 image;
      * a second top-level directory appears under the stage, so extraction
        would scatter into the current directory;
      * a configuration, database, cache or log file is about to be packed.

    A refusal leaves no archive behind. A half-written ZIP is a ZIP that a later
    step can mistake for a package.

.PARAMETER RepoRoot
    The checkout being packaged. The version, the commit and the two frozen
    consumers are all read from here.

.PARAMETER WorkDir
    Private scratch. It must not already exist, so that a second assembly can
    never inherit a first one's bytes; the caller passes two different ones and
    that is what makes the two assemblies independent.

.PARAMETER OutDir
    Where the finished ZIP is written. Nothing else is written here.

.PARAMETER SourceDateEpoch
    Seconds since the Unix epoch, derived from the commit being built. Required.

.PARAMETER OrasPath
    The pinned ORAS client the frozen FFmpeg consumer needs.

.PARAMETER StageRoot
    PACK-ONLY. Pack an already-staged tree, acquiring nothing. This exists so the
    hostile controls can drive the REAL packer -- the same refusals, the same
    ordering, the same clamp -- over a synthetic stage on a host with no network,
    rather than proving a second copy of the packer written for the test. It
    reaches no registry, runs no publish and reads no accepted digest, and
    `zip-controls.py` (Z11) asserts the production workflow never passes it. It
    is modelled on the frozen consumer's own `-GrammarCheck` parameter set.
#>

[CmdletBinding(DefaultParameterSetName = 'Assemble')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Assemble')]
    [string] $RepoRoot,

    [Parameter(Mandatory = $true, ParameterSetName = 'Assemble')]
    [string] $WorkDir,

    [Parameter(Mandatory = $true, ParameterSetName = 'Assemble')]
    [string] $OrasPath,

    [Parameter(ParameterSetName = 'Assemble')]
    [string] $PythonPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'PackOnly')]
    [string] $StageRoot,

    [Parameter(Mandatory = $true, ParameterSetName = 'Assemble')]
    [Parameter(Mandatory = $true, ParameterSetName = 'PackOnly')]
    [string] $OutDir,

    # Deliberately NOT Mandatory. A mandatory parameter that is omitted produces
    # PowerShell's own prompt or a generic binding error, and "the epoch was
    # missing" is the single most important thing this script has to be able to
    # say out loud. It is validated below instead.
    [Parameter(ParameterSetName = 'Assemble')]
    [Parameter(ParameterSetName = 'PackOnly')]
    [int64] $SourceDateEpoch = 0
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# The accepted identities. These are the ruling's values. They are NOT a second
# trust boundary competing with the committed ones: each is asserted to agree
# with the file the frozen consumer actually reads, so a drift in either is a
# refusal rather than a silent preference for whichever was checked last.
# ---------------------------------------------------------------------------
$WEB_PAYLOAD_SHA256 = '4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f'
$ACCEPTED_RUNTIME_SHA256 = 'f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e'

# 1980-01-01T00:00:00Z. The MS-DOS date field in a ZIP local header cannot
# represent anything earlier, so an epoch below this is refused HERE, with a
# sentence that says what is wrong, rather than as an ArgumentOutOfRangeException
# from deep inside the archive writer.
$DOS_EPOCH_FLOOR = 315532800

# The relative layout this slice FREEZES, and that W3's MSI reuses. It mirrors
# W0 §9.1: everything below is package-owned and replaced wholesale on upgrade.
$WEB_SUBDIR = 'web'
$FFMPEG_SUBDIR = 'ffmpeg'
$LICENSES_SUBDIR = 'licenses'

$PACKAGE_PREFIX = 'tesserafin-server'
$RID = 'win-x64'

# ---------------------------------------------------------------------------
# State. §6: the ZIP "ships **no** state. Configuration, database, cache and
# logs are always given by argument."
#
# Named shapes only. A rule broad enough to catch "anything that looks like
# configuration" catches `Resources/Configuration/logging.json`, which is the
# shipped default template and not state, and the only way to keep such a rule
# green is to loosen it until it would miss the real thing.
# ---------------------------------------------------------------------------
$STATE_EXTENSIONS = @('.db', '.db-wal', '.db-shm', '.db-journal', '.log')
$STATE_LEAVES = @(
    'network.xml', 'system.xml', 'encoding.xml', 'branding.xml', 'dlna.xml',
    'migrations.xml', 'livetv.xml', 'notifications.xml', 'hardware.xml',
    'xbmcmetadata.xml'
)
# Directories that are operator-owned under %ProgramData% (W0 §9.1) and must
# therefore never appear inside the package tree at all. Matched only at the
# first level below the top-level directory, so a nested `web/metadata/...` in
# the accepted payload is not mistaken for the server's metadata store.
$STATE_TOP_DIRECTORIES = @(
    'config', 'data', 'cache', 'log', 'logs', 'transcodes', 'root', 'plugins',
    'metadata'
)

# ---------------------------------------------------------------------------

function Deny {
    param([string] $Category, [string] $Message)
    throw ("W2-A2 DENY [{0}] {1}" -f $Category, $Message)
}

function Write-Note {
    param([string] $Message)
    Write-Host ("W2-A2: {0}" -f $Message)
}

function Get-Sha256 {
    param([string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Epoch {
    param([int64] $Epoch)
    if ($Epoch -eq 0) {
        Deny 'source-date-epoch' ('no SOURCE_DATE_EPOCH was given. It is derived from the ' +
            'commit being built and is never defaulted to the clock: an archive timestamped ' +
            'with when it happened to be built is not reproducible.')
    }
    if ($Epoch -lt 0) {
        Deny 'source-date-epoch' ("SOURCE_DATE_EPOCH $Epoch is negative")
    }
    if ($Epoch -lt $DOS_EPOCH_FLOOR) {
        Deny 'source-date-epoch' ("SOURCE_DATE_EPOCH $Epoch is before 1980-01-01, which a ZIP " +
            'timestamp cannot represent')
    }
    # 2107-12-31 is the last date the MS-DOS field can hold.
    if ($Epoch -gt 4354819200) {
        Deny 'source-date-epoch' ("SOURCE_DATE_EPOCH $Epoch is after 2107, which a ZIP " +
            'timestamp cannot represent')
    }
}

# The canonical relative path of a file below a root: forward slashes, no
# leading separator, and never the platform's own idea of one.
function Get-RelativeEntryName {
    param([string] $Root, [string] $FullName)
    $rooted = [System.IO.Path]::GetFullPath($Root)
    if (-not $rooted.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $rooted += [System.IO.Path]::DirectorySeparatorChar
    }
    $full = [System.IO.Path]::GetFullPath($FullName)
    if (-not $full.StartsWith($rooted, [System.StringComparison]::Ordinal)) {
        Deny 'stage' ("'$FullName' is not below the stage root")
    }
    return $full.Substring($rooted.Length).Replace('\', '/')
}

function Test-StateEntry {
    param([string] $Relative)
    $leaf = ($Relative -split '/')[-1]
    $lower = $leaf.ToLowerInvariant()
    foreach ($extension in $STATE_EXTENSIONS) {
        if ($lower.EndsWith($extension)) { return "'$Relative' is a $extension file" }
    }
    if ($STATE_LEAVES -contains $lower) { return "'$Relative' is server configuration" }
    $segments = $Relative -split '/'
    # segments[0] is the one top-level directory; segments[1] is a child of the
    # package root, which is where an operator-owned directory would land.
    if ($segments.Count -ge 3 -and ($STATE_TOP_DIRECTORIES -contains $segments[1].ToLowerInvariant())) {
        return "'$Relative' is inside the operator-owned directory '$($segments[1])'"
    }
    return $null
}

# ---------------------------------------------------------------------------
# The packer
# ---------------------------------------------------------------------------

function Invoke-Pack {
    <#
        Pack the ONE top-level directory under $Stage into $Destination.

        Every property that makes the bytes a function of the contents and
        nothing else is set explicitly here:

          * entry order is an ORDINAL sort of the forward-slash relative paths.
            `Sort-Object` is culture-sensitive and `Get-ChildItem` order is the
            filesystem's, and either would make the archive depend on something
            other than what is in it.
          * every mtime is the clamp, in UTC, not the file's own.
          * modes are fixed: 0755 for .exe, 0644 otherwise, matching
            ci/windows/ffmpeg/package.py, which is how the inner FFmpeg archive
            was already built.
          * no directory entries, for the same reason.
          * one stated compression level, because the level is an input to the
            compressed bytes just as the content is.
    #>
    param(
        [string] $Stage,
        [string] $Destination,
        [int64] $Epoch
    )

    if (-not [System.IO.Directory]::Exists($Stage)) {
        Deny 'stage' ("no stage directory at '$Stage'")
    }

    # --- exactly one top-level directory ------------------------------------
    $topLevel = @(Get-ChildItem -LiteralPath $Stage -Force | Sort-Object -Property Name)
    if ($topLevel.Count -ne 1) {
        $names = ($topLevel | ForEach-Object { $_.Name }) -join ', '
        Deny 'top-level' ("the stage holds $($topLevel.Count) top-level entries ($names); §6 " +
            'requires exactly one top-level directory so extraction never scatters files into ' +
            'the current directory')
    }
    if (-not ($topLevel[0] -is [System.IO.DirectoryInfo])) {
        Deny 'top-level' ("the single top-level entry '$($topLevel[0].Name)' is not a directory")
    }
    $packageName = $topLevel[0].Name
    if ($packageName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        Deny 'top-level' ("the top-level directory name '$packageName' is not a plain name")
    }

    # --- every file, in the one order this packer accepts --------------------
    $files = [System.Collections.Generic.List[string]]::new()
    foreach ($item in [System.IO.Directory]::EnumerateFileSystemEntries(
            $Stage, '*', [System.IO.SearchOption]::AllDirectories)) {
        $info = Get-Item -LiteralPath $item -Force
        if ($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            Deny 'stage' ("'$(Get-RelativeEntryName $Stage $item)' is a reparse point; a package " +
                'tree is files and directories only')
        }
        if ($info -is [System.IO.DirectoryInfo]) { continue }
        $files.Add((Get-RelativeEntryName $Stage $item))
    }
    if ($files.Count -eq 0) { Deny 'stage' 'the stage holds no files' }

    $names = $files.ToArray()
    [Array]::Sort($names, [System.StringComparer]::Ordinal)

    # --- no state, checked before a single byte is compressed ----------------
    $stateFindings = @()
    foreach ($relative in $names) {
        $finding = Test-StateEntry $relative
        if ($finding) { $stateFindings += $finding }
    }
    if ($stateFindings.Count -gt 0) {
        Deny 'state' ('§6 requires the archive to ship no state, and it is about to pack: ' +
            (($stateFindings | Select-Object -First 8) -join '; '))
    }

    # --- pack ----------------------------------------------------------------
    Assert-Epoch $Epoch
    $stamp = [System.DateTimeOffset]::FromUnixTimeSeconds($Epoch).ToUniversalTime()
    # An MS-DOS timestamp stores seconds/2, so it truncates an odd second to the
    # even one below. That is a property of the format, not of this packer: the
    # clamp is still a function of SOURCE_DATE_EPOCH alone and two builds of one
    # commit still agree. It is computed here because the read-back below
    # compares against what a ZIP can actually hold, and comparing against the
    # untruncated clamp would refuse every commit whose committer time happens
    # to be odd -- roughly half of them.
    $clampWall = $stamp.UtcDateTime
    $clampWall = $clampWall.AddTicks(-($clampWall.Ticks % [System.TimeSpan]::TicksPerSecond))
    if ($clampWall.Second % 2 -ne 0) { $clampWall = $clampWall.AddSeconds(-1) }

    $null = [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Destination))
    if ([System.IO.File]::Exists($Destination)) {
        Deny 'output' ("'$Destination' already exists; this packer never overwrites an archive")
    }
    $script:PendingArchive = $Destination

    Write-Note ("packing $($names.Count) files into $([System.IO.Path]::GetFileName($Destination))")
    Write-Note ("clamp $Epoch ($($stamp.ToString('yyyy-MM-ddTHH:mm:ssZ')))")

    $stream = [System.IO.File]::Open(
        $Destination, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($relative in $names) {
                $source = [System.IO.Path]::Combine($Stage, $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $stamp
                # 0o755 / 0o644, written in hex because a bare `0o755` inside a
                # statement block is parsed as a command name, not a literal.
                $mode = if ($relative.ToLowerInvariant().EndsWith('.exe')) { 0x1ED } else { 0x1A4 }
                # 0x8000 is S_IFREG. The low 16 bits are the MS-DOS attribute
                # word, left at zero: FILE_ATTRIBUTE_NORMAL is not representable
                # there as anything but the absence of every flag, and reading
                # the real attributes back would make the archive depend on the
                # filesystem it was staged on.
                $entry.ExternalAttributes = ((0x8000 -bor $mode) -shl 16)
                $target = $entry.Open()
                try {
                    $input = [System.IO.File]::OpenRead($source)
                    try { $input.CopyTo($target) } finally { $input.Dispose() }
                } finally { $target.Dispose() }
            }
        } finally {
            $archive.Dispose()
        }
    } finally {
        $stream.Dispose()
    }

    # --- read it back, because a packer that cannot be read is not a packer ---
    $verified = 0
    $readback = [System.IO.Compression.ZipFile]::OpenRead($Destination)
    try {
        if ($readback.Entries.Count -ne $names.Count) {
            Deny 'archive' ("the archive holds $($readback.Entries.Count) entries, $($names.Count) were packed")
        }
        for ($index = 0; $index -lt $names.Count; $index++) {
            $entry = $readback.Entries[$index]
            if ($entry.FullName -cne $names[$index]) {
                Deny 'archive' ("entry $index is '$($entry.FullName)', the packed order says '$($names[$index])'")
            }
            if (-not $entry.FullName.StartsWith("$packageName/", [System.StringComparison]::Ordinal)) {
                Deny 'archive' ("'$($entry.FullName)' would extract outside '$packageName/'")
            }
            # A ZIP stores an MS-DOS wall clock with no zone, and .NET's getter
            # hands it back with the READER's local offset attached. The bytes
            # therefore depend only on the wall clock that was written, which is
            # the clamp in UTC -- so the comparison is between wall clocks, and
            # comparing instants here would fail on every host that is not UTC
            # while the archive was perfectly deterministic.
            if ($entry.LastWriteTime.DateTime -ne $clampWall) {
                Deny 'archive' ("'$($entry.FullName)' carries wall clock $($entry.LastWriteTime.DateTime.ToString('s')), not the clamp $($clampWall.ToString('s'))")
            }
            $source = [System.IO.Path]::Combine($Stage, $names[$index].Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            $expected = Get-Sha256 $source
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                $entryStream = $entry.Open()
                try {
                    $actual = [System.BitConverter]::ToString($sha.ComputeHash($entryStream)).Replace('-', '').ToLowerInvariant()
                } finally { $entryStream.Dispose() }
            } finally { $sha.Dispose() }
            if ($actual -cne $expected) {
                Deny 'archive' ("'$($entry.FullName)' unpacks to $actual, the staged file is $expected")
            }
            $verified++
        }
    } finally {
        $readback.Dispose()
    }

    $digest = Get-Sha256 $Destination
    $size = (Get-Item -LiteralPath $Destination).Length
    Write-Note ("verified $verified entries unpack to the staged bytes")
    Write-Note ("archive sha256 $digest")
    Write-Note ("archive bytes  $size")
    return [pscustomobject]@{
        Path = $Destination
        PackageName = $packageName
        Entries = $names.Count
        Sha256 = $digest
        Size = $size
    }
}

# ---------------------------------------------------------------------------
# Acquisition and staging
# ---------------------------------------------------------------------------

function Resolve-Python {
    param([string] $Preferred)
    if ($Preferred) {
        if (-not [System.IO.File]::Exists($Preferred)) {
            Deny 'prerequisite' ("no python at '$Preferred'")
        }
        return $Preferred
    }
    foreach ($candidate in @('python3', 'python')) {
        $found = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { return $found.Source }
    }
    Deny 'prerequisite' 'no python on PATH; the canonical tree digest cannot be computed'
}

function Get-DeclaredVersion {
    <#
        The version the project already declares for this commit, from the one
        canonical source docker/version-contract.sh reads. This script does not
        run that contract: it derives no tag, needs no registry and must work on
        a runner where the contract's `date -u -d` and `mapfile` are not
        guaranteed. It reads the same file, with the same rule, and refuses
        anything that is not a MAJOR.MINOR.PATCH SemVer core -- which is exactly
        what the contract does before it derives anything.
    #>
    param([string] $Root)
    $path = [System.IO.Path]::Combine($Root, 'SharedVersion.cs')
    if (-not [System.IO.File]::Exists($path)) {
        Deny 'version' ("no canonical version source at '$path'")
    }
    $text = [System.IO.File]::ReadAllText($path)
    $match = [regex]::Match($text, '\[assembly: ?AssemblyVersion\("([^"]*)"\)\]')
    if (-not $match.Success) {
        Deny 'version' ("no [assembly: AssemblyVersion(...)] in '$path'")
    }
    $version = $match.Groups[1].Value
    if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        Deny 'version' ("the declared version '$version' is not a MAJOR.MINOR.PATCH SemVer core")
    }
    return $version
}

function Get-ServerCommit {
    param([string] $Root)
    $commit = (& git -C $Root rev-parse HEAD 2>&1 | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0) {
        Deny 'provenance' ("'$Root' has no readable git commit: $commit")
    }
    $commit = ([string]$commit).Trim()
    if ($commit -notmatch '^[0-9a-f]{40}$') {
        Deny 'provenance' ("'$commit' is not a full 40-character lowercase commit")
    }
    return $commit
}

function Assert-SelfContained {
    <#
        A publish tree that is not self-contained produces a ZIP that needs a
        .NET runtime the operator has to install, which is precisely the thing
        the portable ZIP exists not to require. Three independent statements are
        required, because each alone has a way of being true by accident.
    #>
    param([string] $Publish)

    foreach ($required in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
        $path = [System.IO.Path]::Combine($Publish, $required)
        if (-not [System.IO.File]::Exists($path)) {
            Deny 'self-contained' ("the publish tree carries no '$required'; it is not a " +
                'self-contained win-x64 publish')
        }
    }

    $configPath = [System.IO.Path]::Combine($Publish, 'tesserafin.runtimeconfig.json')
    if (-not [System.IO.File]::Exists($configPath)) {
        Deny 'self-contained' 'the publish tree carries no tesserafin.runtimeconfig.json'
    }
    $config = [System.IO.File]::ReadAllText($configPath) | ConvertFrom-Json
    if (-not ($config.PSObject.Properties.Name -contains 'runtimeOptions')) {
        Deny 'self-contained' 'tesserafin.runtimeconfig.json declares no runtimeOptions'
    }
    $options = $config.runtimeOptions
    foreach ($shared in @('framework', 'frameworks')) {
        if ($options.PSObject.Properties.Name -contains $shared) {
            Deny 'self-contained' ("tesserafin.runtimeconfig.json declares a shared '$shared'; " +
                'a self-contained publish declares none')
        }
    }
    if (-not ($options.PSObject.Properties.Name -contains 'includedFrameworks')) {
        Deny 'self-contained' ('tesserafin.runtimeconfig.json declares no includedFrameworks; ' +
            'a self-contained publish carries its runtime with it')
    }

    $exe = [System.IO.Path]::Combine($Publish, 'tesserafin.exe')
    if (-not [System.IO.File]::Exists($exe)) {
        Deny 'self-contained' 'the publish tree carries no tesserafin.exe'
    }
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        Deny 'self-contained' 'tesserafin.exe is not an MZ image'
    }
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -le 0 -or ($peOffset + 6) -ge $bytes.Length) {
        Deny 'self-contained' 'tesserafin.exe has no readable PE header offset'
    }
    if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        Deny 'self-contained' 'tesserafin.exe carries no PE signature'
    }
    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x8664) {
        Deny 'self-contained' ("tesserafin.exe is PE machine 0x{0:x4}, not x64 (0x8664)" -f $machine)
    }

    Write-Note 'publish tree is self-contained win-x64: host components present, runtimeconfig declares no shared framework, tesserafin.exe is PE x64'
}

function Copy-TreeInto {
    param([string] $Source, [string] $Destination)
    $null = [System.IO.Directory]::CreateDirectory($Destination)
    foreach ($item in [System.IO.Directory]::EnumerateFileSystemEntries(
            $Source, '*', [System.IO.SearchOption]::AllDirectories)) {
        $info = Get-Item -LiteralPath $item -Force
        if ($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            Deny 'stage' ("'$item' is a reparse point and is not copied into a package tree")
        }
        $relative = Get-RelativeEntryName $Source $item
        $target = [System.IO.Path]::Combine($Destination, $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if ($info -is [System.IO.DirectoryInfo]) {
            $null = [System.IO.Directory]::CreateDirectory($target)
            continue
        }
        $null = [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target))
        [System.IO.File]::Copy($item, $target, $false)
        # The staged copy is writable: the frozen FFmpeg consumer marks its
        # output read-only, and a read-only staged tree cannot be cleaned up.
        $copied = Get-Item -LiteralPath $target -Force
        if ($copied.IsReadOnly) { $copied.IsReadOnly = $false }
    }
}

function Expand-InnerArchive {
    <#
        The accepted FFmpeg runtime archive, whose bytes were verified against
        `runtimeSha256` BEFORE this is called. Extracted with a reader that
        refuses everything a ZIP entry can do that a runtime has no business
        doing, because "it is the accepted digest" is a statement about the
        archive and not about where its entries would land.
    #>
    param([string] $Archive, [string] $Destination)
    $null = [System.IO.Directory]::CreateDirectory($Destination)
    $rooted = [System.IO.Path]::GetFullPath($Destination)
    if (-not $rooted.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $rooted += [System.IO.Path]::DirectorySeparatorChar
    }
    $count = 0
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name.EndsWith('/')) { continue }
            if ($name.StartsWith('/') -or $name.StartsWith('\') -or $name -match '^[A-Za-z]:') {
                Deny 'ffmpeg' ("absolute path in the accepted runtime archive: '$name'")
            }
            if ($name.Contains('\')) {
                Deny 'ffmpeg' ("backslash in the accepted runtime archive path: '$name'")
            }
            foreach ($segment in $name.Split('/')) {
                if ($segment -eq '' -or $segment -eq '.' -or $segment -eq '..') {
                    Deny 'ffmpeg' ("traversal or empty segment in the accepted runtime archive: '$name'")
                }
            }
            $target = [System.IO.Path]::GetFullPath(
                [System.IO.Path]::Combine($Destination, $name.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
            if (-not $target.StartsWith($rooted, [System.StringComparison]::Ordinal)) {
                Deny 'ffmpeg' ("'$name' would extract outside the runtime directory")
            }
            if ([System.IO.File]::Exists($target)) {
                Deny 'ffmpeg' ("the accepted runtime archive carries '$name' twice")
            }
            $null = [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target))
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
            $count++
        }
    } finally {
        $zip.Dispose()
    }
    if ($count -eq 0) { Deny 'ffmpeg' 'the accepted runtime archive carries no files' }
    Write-Note ("extracted $count files from the accepted FFmpeg runtime archive")
    return $count
}

# ===========================================================================

$archiveResult = $null
# The one path this run may remove on a refusal. A cleanup that deleted every
# *.zip in the caller's output directory would contradict this script's own rule
# that it never overwrites an archive, and would make a refusal destructive.
$script:PendingArchive = $null
try {
    if ($PSCmdlet.ParameterSetName -eq 'PackOnly') {
        # No registry, no publish, no accepted digest. The same packer, the same
        # refusals, over a stage the caller already built.
        Assert-Epoch $SourceDateEpoch
        $stageRootFull = [System.IO.Path]::GetFullPath($StageRoot)
        $top = @(Get-ChildItem -LiteralPath $stageRootFull -Force)
        $name = if ($top.Count -ge 1) { $top[0].Name } else { 'package' }
        $destination = [System.IO.Path]::Combine(
            [System.IO.Path]::GetFullPath($OutDir), "$name.zip")
        $archiveResult = Invoke-Pack -Stage $stageRootFull -Destination $destination -Epoch $SourceDateEpoch
        Write-Note ("pack-only: $($archiveResult.Path)")
        exit 0
    }

    Assert-Epoch $SourceDateEpoch

    $repo = [System.IO.Path]::GetFullPath($RepoRoot)
    if (-not [System.IO.Directory]::Exists($repo)) { Deny 'prerequisite' "no repository at '$repo'" }
    $work = [System.IO.Path]::GetFullPath($WorkDir)
    if ([System.IO.Directory]::Exists($work) -and
        @([System.IO.Directory]::EnumerateFileSystemEntries($work)).Count -gt 0) {
        Deny 'work-dir' ("'$work' is not empty. Two assemblies are only independent if neither " +
            'can inherit the other''s bytes, so this script never reuses a work directory.')
    }
    $null = [System.IO.Directory]::CreateDirectory($work)
    $out = [System.IO.Path]::GetFullPath($OutDir)
    $null = [System.IO.Directory]::CreateDirectory($out)

    $python = Resolve-Python $PythonPath
    if (-not [System.IO.File]::Exists($OrasPath)) {
        Deny 'prerequisite' ("no ORAS client at '$OrasPath'")
    }

    $version = Get-DeclaredVersion $repo
    $commit = Get-ServerCommit $repo
    $packageName = "{0}_{1}_{2}" -f $PACKAGE_PREFIX, $version, $RID
    Write-Note ("version $version, commit $commit")
    Write-Note ("package $packageName")

    $webConsumer = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'w2', 'consume-web-payload.ps1')
    $runtimeConsumer = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'runtime-retention', 'consume.ps1')
    $acceptedJson = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'runtime-retention', 'accepted-runtime.json')
    $treeDigestScript = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'w2', 'pkg-tree-digest.py')
    foreach ($required in @($webConsumer, $runtimeConsumer, $acceptedJson, $treeDigestScript)) {
        if (-not [System.IO.File]::Exists($required)) {
            Deny 'prerequisite' ("the frozen input '$required' is missing")
        }
    }

    # ── 1. the accepted Web payload, through the frozen W2-A0 consumer ───────
    $webOut = [System.IO.Path]::Combine($work, 'web')
    $webEvidence = [System.IO.Path]::Combine($work, 'web-evidence.json')
    $webArguments = @{
        OutputPath = $webOut
        EvidencePath = $webEvidence
        PythonPath = $python
    }
    & $webConsumer @webArguments
    if ($LASTEXITCODE -ne 0) { Deny 'web-payload' 'the frozen W2-A0 consumer did not accept a payload' }
    if (-not [System.IO.File]::Exists($webEvidence)) {
        Deny 'web-payload' 'the frozen W2-A0 consumer wrote no evidence document'
    }
    $webRecord = [System.IO.File]::ReadAllText($webEvidence) | ConvertFrom-Json
    if ($webRecord.contract -cne 'accepted') {
        Deny 'web-payload' ("the payload was acquired under the '$($webRecord.contract)' contract")
    }
    if ([string]$webRecord.treeDigest -cne $WEB_PAYLOAD_SHA256) {
        Deny 'web-payload' ("the payload hashes to $($webRecord.treeDigest), the accepted " +
            "WEB_PAYLOAD_SHA256 is $WEB_PAYLOAD_SHA256")
    }
    $webEpoch = [int64]$webRecord.sourceDateEpoch
    if ($webEpoch -le 0) { Deny 'web-payload' 'the payload records no usable sourceDateEpoch' }
    Write-Note ("web payload $($webRecord.treeDigest) at epoch $webEpoch, revision $($webRecord.webRevision)")

    # ── 2. the accepted FFmpeg runtime, through the frozen W1/W2-A1 consumer ─
    $accepted = [System.IO.File]::ReadAllText($acceptedJson) | ConvertFrom-Json
    if ([string]$accepted.runtimeSha256 -cne $ACCEPTED_RUNTIME_SHA256) {
        Deny 'ffmpeg' ("the committed acceptance manifest pins runtime $($accepted.runtimeSha256), " +
            "the ruling pins $ACCEPTED_RUNTIME_SHA256")
    }
    $runtimeWork = [System.IO.Path]::Combine($work, 'ffmpeg-work')
    $runtimeOut = [System.IO.Path]::Combine($work, 'ffmpeg-out')
    & $runtimeConsumer -AcceptedManifest $acceptedJson -WorkDir $runtimeWork -OutDir $runtimeOut -OrasPath $OrasPath
    if ($LASTEXITCODE -ne 0) { Deny 'ffmpeg' 'the frozen W1 consumer did not accept a runtime' }

    $innerName = [System.IO.Path]::GetFileName($accepted.runtimePath)
    $innerArchive = [System.IO.Path]::Combine($runtimeOut, $innerName)
    if (-not [System.IO.File]::Exists($innerArchive)) {
        Deny 'ffmpeg' ("the frozen consumer exposed no runtime archive at '$innerArchive'")
    }
    $innerDigest = Get-Sha256 $innerArchive
    if ($innerDigest -cne $ACCEPTED_RUNTIME_SHA256) {
        Deny 'ffmpeg' ("the inner runtime archive hashes to $innerDigest, the accepted " +
            "runtimeSha256 is $ACCEPTED_RUNTIME_SHA256")
    }
    Write-Note ("ffmpeg runtime $innerDigest ($($accepted.ffmpegBuildRevision), $($accepted.licence))")

    # ── 3. the server, published self-contained for win-x64 ──────────────────
    $publish = [System.IO.Path]::Combine($work, 'publish')
    $project = [System.IO.Path]::Combine($repo, 'Tesserafin.Server', 'Tesserafin.Server.csproj')
    if (-not [System.IO.File]::Exists($project)) {
        Deny 'publish' ("no server project at '$project'")
    }
    & dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $publish
    if ($LASTEXITCODE -ne 0) { Deny 'publish' 'dotnet publish failed' }
    Assert-SelfContained $publish

    # ── 4. one top-level directory, at the relative paths W3's MSI reuses ────
    $stage = [System.IO.Path]::Combine($work, 'stage')
    $packageRoot = [System.IO.Path]::Combine($stage, $packageName)
    $null = [System.IO.Directory]::CreateDirectory($packageRoot)

    Copy-TreeInto $publish $packageRoot
    Copy-TreeInto $webOut ([System.IO.Path]::Combine($packageRoot, $WEB_SUBDIR))
    $null = Expand-InnerArchive $innerArchive ([System.IO.Path]::Combine($packageRoot, $FFMPEG_SUBDIR))

    $licenses = [System.IO.Path]::Combine($packageRoot, $LICENSES_SUBDIR)
    $null = [System.IO.Directory]::CreateDirectory($licenses)
    $serverLicence = [System.IO.Path]::Combine($repo, 'LICENSE')
    if (-not [System.IO.File]::Exists($serverLicence)) { Deny 'licensing' 'the checkout carries no LICENSE' }
    [System.IO.File]::Copy($serverLicence, [System.IO.Path]::Combine($licenses, 'LICENSE'), $false)

    # The FFmpeg licences, the third-party notices, the capability manifest and
    # the build configuration already ship INSIDE the accepted runtime archive,
    # so they are not copied a second time: two copies of a licence file are two
    # things that can disagree. What is done instead is to hash the extracted
    # copies against the same acceptance manifest, which turns "the archive
    # contains its licences" from an assumption into a measurement.
    $unit = [System.IO.Path]::Combine($runtimeWork, 'unit')
    $ffmpegRoot = [System.IO.Path]::Combine($packageRoot, $FFMPEG_SUBDIR)
    foreach ($pair in @(
            @{ Leaf = 'THIRD-PARTY-NOTICES.md'; Digest = [string]$accepted.noticesSha256 },
            @{ Leaf = 'capability.json'; Digest = [string]$accepted.capabilitySha256 })) {
        $path = [System.IO.Path]::Combine($ffmpegRoot, $pair.Leaf)
        if (-not [System.IO.File]::Exists($path)) {
            Deny 'licensing' ("the accepted runtime archive delivered no '$($pair.Leaf)'")
        }
        $digest = Get-Sha256 $path
        if ($digest -cne $pair.Digest) {
            Deny 'licensing' ("the extracted '$($pair.Leaf)' hashes to $digest, the acceptance " +
                "manifest pins $($pair.Digest)")
        }
    }
    $licenceFiles = @(Get-ChildItem -LiteralPath ([System.IO.Path]::Combine($ffmpegRoot, 'LICENSES')) -File -ErrorAction SilentlyContinue)
    if ($licenceFiles.Count -ne [int]$accepted.licenceFileCount) {
        Deny 'licensing' ("the extracted runtime carries $($licenceFiles.Count) licence files, " +
            "the acceptance manifest records $($accepted.licenceFileCount)")
    }

    # The SBOM is the one description retained BESIDE the runtime archive rather
    # than inside it, so it is the one file taken from the verified retention
    # unit and re-hashed against the acceptance manifest.
    $ffmpegLicenses = [System.IO.Path]::Combine($licenses, 'ffmpeg')
    $null = [System.IO.Directory]::CreateDirectory($ffmpegLicenses)
    $sbomSource = [System.IO.Path]::Combine($unit, 'delivered', 'sbom.cdx.json')
    if (-not [System.IO.File]::Exists($sbomSource)) {
        Deny 'licensing' 'the verified retention unit carries no delivered/sbom.cdx.json'
    }
    $sbomDigest = Get-Sha256 $sbomSource
    if ($sbomDigest -cne [string]$accepted.sbomSha256) {
        Deny 'licensing' ("the SBOM hashes to $sbomDigest, the acceptance manifest pins " +
            "$($accepted.sbomSha256)")
    }
    $sbomTarget = [System.IO.Path]::Combine($ffmpegLicenses, 'sbom.cdx.json')
    [System.IO.File]::Copy($sbomSource, $sbomTarget, $false)
    $sbomCopy = Get-Item -LiteralPath $sbomTarget -Force
    if ($sbomCopy.IsReadOnly) { $sbomCopy.IsReadOnly = $false }
    Write-Note ("licences verified: $($licenceFiles.Count) component licences, the notices and " +
        "the capability manifest inside $FFMPEG_SUBDIR/, the SBOM at $LICENSES_SUBDIR/ffmpeg/")

    # ── 5. the staged Web tree is still the accepted one ─────────────────────
    #
    # Hashed at the epoch the WEB build recorded for itself, never at this
    # commit's: the digest of an INPUT must not move when the server commit
    # moves. That is the rule ci/package/lib.sh states and the reason the pinned
    # digest stays valid until the payload itself changes.
    $stagedWeb = [System.IO.Path]::Combine($packageRoot, $WEB_SUBDIR)
    $stagedDigest = ([string](& $python $treeDigestScript $stagedWeb $webEpoch | Select-Object -Last 1)).Trim()
    if ($LASTEXITCODE -ne 0) { Deny 'web-payload' 'the staged web tree could not be hashed' }
    if ($stagedDigest -cne $WEB_PAYLOAD_SHA256) {
        Deny 'web-payload' ("the STAGED web tree hashes to $stagedDigest, the accepted " +
            "WEB_PAYLOAD_SHA256 is $WEB_PAYLOAD_SHA256")
    }
    Write-Note ("staged web tree still hashes to $stagedDigest")

    # ── 6. the provenance manifest ───────────────────────────────────────────
    #
    # It records identities and digests only. No work directory, no output
    # directory, no run identifier, no runner name and no host path: a
    # provenance document that carries where it was built is a document whose
    # bytes differ between two builds of the same commit.
    $provenance = [ordered]@{
        schemaVersion = 1
        manifest = 'tesserafin-windows-server-zip'
        packageName = $PACKAGE_PREFIX
        packageVersion = $version
        packageFormat = 'zip'
        archiveName = "$packageName.zip"
        topLevelDirectory = $packageName
        runtimeIdentifier = $RID
        selfContained = $true
        serverCommit = $commit
        serverRepository = 'https://github.com/tesserafin-project/tesserafin'
        sourceDateEpoch = $SourceDateEpoch
        buildTimestamp = [System.DateTimeOffset]::FromUnixTimeSeconds($SourceDateEpoch).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        web = [ordered]@{
            image = ('{0}/{1}@{2}' -f $webRecord.registry, $webRecord.repository, $webRecord.reference)
            revision = [string]$webRecord.webRevision
            payloadSha256 = [string]$webRecord.treeDigest
            sourceDateEpoch = $webEpoch
            relativePath = $WEB_SUBDIR
        }
        ffmpegRuntime = [ordered]@{
            reference = [string]$accepted.reference
            manifestDigest = [string]$accepted.manifestDigest
            layerDigest = [string]$accepted.layerDigest
            buildRevision = [string]$accepted.ffmpegBuildRevision
            upstreamCommit = [string]$accepted.ffmpegUpstreamCommit
            archiveSha256 = $innerDigest
            relativePath = $FFMPEG_SUBDIR
            licence = [string]$accepted.licence
            correspondingSourcePath = [string]$accepted.correspondingSourcePath
            correspondingSourceSha256 = [string]$accepted.correspondingSourceSha256
            correspondingSourceStreamSha256 = [string]$accepted.correspondingSourceStreamSha256
            correspondingSourceLocation = ('the retained unit at {0}; this archive carries the ' +
                'binary and its licences, and the complete corresponding source is retained ' +
                'beside it under the same immutable digest') -f $accepted.reference
        }
        licensing = [ordered]@{
            # The same two expressions ci/package/assemble-payload.sh and
            # ci/package/build-all.sh already declare for the Linux packages, for
            # the same two bodies of code: the server's own LICENSE is GPLv2
            # "either version 2 of the License, or (at your option) any later
            # version", and the runtime is built --enable-gpl --enable-version3.
            # Collapsing the two into one expression would describe neither.
            server = 'GPL-2.0-or-later'
            ffmpegRuntime = [string]$accepted.licence
            spdxExpression = 'GPL-2.0-or-later AND GPL-3.0-or-later'
            sbomSha256 = [string]$accepted.sbomSha256
            noticesSha256 = [string]$accepted.noticesSha256
            relativePath = $LICENSES_SUBDIR
        }
        shipsNoState = $true
        containerRuntime = 'none'
        actionsArtifactHandoff = $false
        signed = $false
    }
    $provenanceJson = ($provenance | ConvertTo-Json -Depth 6) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText(
        [System.IO.Path]::Combine($licenses, 'provenance.json'),
        $provenanceJson + "`n",
        (New-Object System.Text.UTF8Encoding($false)))

    # ── 7. pack ──────────────────────────────────────────────────────────────
    $destination = [System.IO.Path]::Combine($out, "$packageName.zip")
    $archiveResult = Invoke-Pack -Stage $stage -Destination $destination -Epoch $SourceDateEpoch
    if ($archiveResult.PackageName -cne $packageName) {
        Deny 'top-level' ("the archive's top-level directory is '$($archiveResult.PackageName)'")
    }
    Write-Note ("assembled $($archiveResult.Path)")
    exit 0
} catch {
    $message = $_.Exception.Message
    if (-not $message.StartsWith('W2-A2 DENY')) { $message = "W2-A2 DENY [unexpected] $message" }
    [Console]::Error.WriteLine($message)
    exit 1
} finally {
    # A refusal leaves no archive. A half-written ZIP is a ZIP a later step can
    # mistake for a package.
    if ($null -eq $archiveResult -and $script:PendingArchive -and
        (Test-Path -LiteralPath $script:PendingArchive -PathType Leaf)) {
        Remove-Item -LiteralPath $script:PendingArchive -Force -ErrorAction SilentlyContinue
    }
}
