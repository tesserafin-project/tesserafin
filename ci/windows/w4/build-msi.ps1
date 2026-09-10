#Requires -Version 7.2
<#
.SYNOPSIS
    Build the W4-A0 Tesserafin MSI from an already-staged, accepted win-x64
    package layout, and refuse to build one at all if the authoring and the
    accepted layout do not agree.

.DESCRIPTION
    W4-A0 (#234). `packaging/windows/msi/Tesserafin.wxs` is the authoring; this
    script is the only thing that invokes it. It exists so that the one property
    a second statement of the layout can break -- the MSI installing the server
    at paths the accepted package does not use -- is a refusal here rather than
    a service that fails to start on an operator's machine.

    What it does, in the order the checks can first be made:

      * pins and installs the WiX toolset by exact version, and -- since
        W4-A2-R1 (#234) -- the WixToolset.Util.wixext extension the §4 failure
        actions are authored with. BOTH pins live on a `wix` command line and
        NOT in Directory.Packages.props. That is not a preference: `wix` is a
        `dotnet tool` and there is no `.wixproj` anywhere in this repository, so
        nothing on this path is a NuGet restore that central package management
        could reach. The extension is acquired by `wix extension add -g` at the
        exact version, and the installed version is then READ BACK and refused
        if it is not the one asked for, exactly as the toolset version is;
      * reads the package layout out of `ci/windows/w2/tesserafin-server-service.ps1`
        -- the accepted W2-A5 script, which is what actually registers the
        service for the portable ZIP -- and refuses if the authoring's paths
        disagree with it. The layout is read, never restated;
      * reads the version out of SharedVersion.cs, the same canonical source
        `ci/windows/w2/assemble-server-zip.ps1` reads, and refuses anything that
        is not a MAJOR.MINOR.PATCH SemVer core, because `Package/@Version` is a
        Windows Installer ProductVersion and cannot carry a prerelease suffix;
      * refuses a stage that is missing the server executable, the Web tree or
        the FFmpeg runtime, so "the MSI contains the accepted layout" cannot be
        satisfied by an empty directory;
      * refuses a stage that carries operator state, because everything under
        `%ProgramData%` is operator-owned (W0 §9.1) and a package that shipped a
        config or a database would overwrite one.

    A refusal leaves no MSI behind.

.PARAMETER RepoRoot
    The checkout being packaged. The authoring, the accepted layout constants
    and the version are all read from here.

.PARAMETER StageRoot
    The extracted W2 package directory -- one top-level directory holding
    tesserafin.exe, web/, ffmpeg/ and licenses/. Produced by the frozen W2-A2
    assembler; this script acquires nothing itself.

.PARAMETER HarvestRoot
    Where the harvested half of the payload lives: the accepted stage minus the
    server executable and minus the portable ZIP's service script. It is
    materialised here on first use and REUSED by every later build, so the five
    packages this slice builds share one copy rather than five.

    It is a separate directory rather than an exclude list inside the authoring
    because WiX harvesting is a LINKER behaviour: an exclude that failed to
    match would deliver the executable twice into the same directory, and it
    would do so on the runner, after the publish, rather than anywhere the
    authoring could be checked first.

.PARAMETER OutPath
    Where the MSI is written.

.PARAMETER Mutation
    CONTROL-ONLY. `none` builds the real package. Every other value builds a
    deliberately broken one for a single hostile control -- four from W4-A0 over
    containment, the argument list and the uninstall, three from W4-A2 over the
    W0 §4 recovery policy, four from W4-A3 over the W0 §9.3 ACLs, and two from
    W4-A4 over what a MajorUpgrade does to the service registration and to
    operator state -- so those
    controls drive the REAL authoring
    rather than a second copy of it written for the test -- the same reason the frozen W2-A2 assembler carries a
    PACK-ONLY parameter set. `ci/windows/w4/msi-controls.py` asserts the hosted
    acceptance build passes nothing but `none`.

.PARAMETER PatchBump
    W4-A4 (#234). How many to add to the PATCH field of the version this script
    already reads out of SharedVersion.cs, so that W4-A4 can build two packages
    from ONE commit whose only difference is that the second's ProductVersion is
    higher than the first's.

    It is a BUMP and not a version, deliberately. The W4-A4 ruling says to bump
    the version "only via the existing preprocessor / SharedVersion read" and not
    to edit SharedVersion.cs, and a `-Version` parameter would be exactly what
    the frozen assembler's own control (`zip-controls.py` Z11) and
    `msi-controls.py`'s FORBIDDEN_BUILDER_PARAMETERS both refuse: the identity of
    what is packaged supplied at call time instead of travelling with the commit.
    A bump cannot do that. MAJOR, MINOR and PATCH all still come from the commit;
    the only thing a caller can say is "and one more than that" -- which is
    exactly and only what proving MajorUpgrade needs.

    The default is 0, so every earlier W4 slice's call is unchanged, and the
    result is re-checked against the same SemVer-core rule the declared version
    is, and against being higher than what it was bumped from.

.PARAMETER WixVersion
    The pinned WiX toolset version. W0 §5.4 records that this is a value a build
    must pin and re-read, exactly like a component checksum.

.PARAMETER UtilExtensionVersion
    The pinned `WixToolset.Util.wixext` version -- the extension that carries
    `util:ServiceConfig`, which W4-A2-R1 (#234) authorised in place of the core
    `ServiceConfigFailureActions` element after `MsiConfigureServices` answered
    MSI error 1939 for it. It is pinned and re-read for the same reason the
    toolset is, and it ships in lockstep with the toolset, so the two default to
    the same version rather than drifting apart silently.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $RepoRoot,
    [Parameter(Mandatory = $true)] [string] $StageRoot,
    [Parameter(Mandatory = $true)] [string] $HarvestRoot,
    [Parameter(Mandatory = $true)] [string] $OutPath,
    [ValidateSet('none', 'no-exe', 'no-service-flag', 'no-path-flags', 'no-service-remove',
        'no-util-config', 'delay-not-60s', 'third-action-restart',
        'acl-users-write', 'acl-no-service-grant', 'acl-install-writable',
        'upgrade-no-service', 'upgrade-wipes-state')]
    [string] $Mutation = 'none',
    [ValidateRange(0, 1)] [int] $PatchBump = 0,
    [ValidateNotNullOrEmpty()] [string] $WixVersion = '6.0.2',
    [ValidateNotNullOrEmpty()] [string] $UtilExtensionVersion = '6.0.2'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Deny {
    param([Parameter(Mandatory = $true)] [string] $Reason,
          [Parameter(Mandatory = $true)] [string] $Detail)
    Write-Host "W4-A0 BUILD REFUSED [$Reason]: $Detail"
    exit 1
}

$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$stage = [System.IO.Path]::GetFullPath($StageRoot)
if (-not [System.IO.Directory]::Exists($stage)) { Deny 'stage' "no stage directory at '$stage'" }

$authoring = [System.IO.Path]::Combine($repo, 'packaging', 'windows', 'msi', 'Tesserafin.wxs')
if (-not [System.IO.File]::Exists($authoring)) { Deny 'authoring' "no authoring at '$authoring'" }

# ---------------------------------------------------------------------------
# The accepted layout, READ from the script that registers the service for the
# portable ZIP. W3's probe reads the same three constants from the same file for
# the same reason: a second statement of the layout in this file could disagree
# with the one the accepted package actually uses, and the disagreement would
# surface much later as a service that starts and finds no Web tree.
# ---------------------------------------------------------------------------
$acceptedScript = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'w2', 'tesserafin-server-service.ps1')
if (-not [System.IO.File]::Exists($acceptedScript)) {
    Deny 'layout' "no accepted W2-A5 service script at '$acceptedScript'"
}
$acceptedText = [System.IO.File]::ReadAllText($acceptedScript)

function Get-AcceptedConstant {
    param([Parameter(Mandatory = $true)] [string] $Name)
    $match = [regex]::Match($acceptedText, "(?m)^\`$$Name\s*=\s*'([^']+)'\s*$")
    if (-not $match.Success) {
        Deny 'layout' ("the accepted W2-A5 service script does not define `$$Name, so the layout " +
            'the MSI must install cannot be read from the script that registers it')
    }
    return $match.Groups[1].Value
}

# Backslashes: the constants are written with forward slashes so the accepted
# script can join them portably. The MSI states Windows paths.
$serverRelativeExe = (Get-AcceptedConstant 'SERVER_RELATIVE_EXE') -replace '/', '\'
$webRelativeDir = (Get-AcceptedConstant 'WEB_RELATIVE_DIR') -replace '/', '\'
$ffmpegRelativeExe = (Get-AcceptedConstant 'FFMPEG_RELATIVE_EXE') -replace '/', '\'
$serviceName = Get-AcceptedConstant 'SERVICE_NAME'

# ---------------------------------------------------------------------------
# The authoring has to agree with all four, and it is checked by reading the
# authoring rather than by trusting that it was written to match. Each of these
# appears in Tesserafin.wxs as literal text; if any is edited without the
# accepted script being edited too, the build stops here.
# ---------------------------------------------------------------------------
$authoringText = [System.IO.File]::ReadAllText($authoring)
$required = [ordered]@{
    "the server executable source '$serverRelativeExe'" = "Source=`"`$(var.StageRoot)\$serverRelativeExe`""
    "the service name '$serviceName'"                   = "Name=`"$serviceName`""
    "the web directory argument '$webRelativeDir'"      = "--webdir &quot;[INSTALLFOLDER]$webRelativeDir&quot;"
    "the ffmpeg argument '$ffmpegRelativeExe'"          = "--ffmpeg &quot;[INSTALLFOLDER]$ffmpegRelativeExe&quot;"
}
foreach ($what in $required.Keys) {
    if (-not $authoringText.Contains($required[$what])) {
        Deny 'layout' ("the authoring does not state $what as the accepted W2-A5 script defines it; " +
            "expected the literal text: $($required[$what])")
    }
}

# ---------------------------------------------------------------------------
# The stage. It must hold the accepted layout, and it must hold no state.
# ---------------------------------------------------------------------------
$stageExe = [System.IO.Path]::Combine($stage, $serverRelativeExe)
$stageWeb = [System.IO.Path]::Combine($stage, $webRelativeDir)
$stageFfmpeg = [System.IO.Path]::Combine($stage, $ffmpegRelativeExe)
if (-not [System.IO.File]::Exists($stageExe)) { Deny 'stage' "the stage has no '$serverRelativeExe'" }
if (-not [System.IO.Directory]::Exists($stageWeb)) { Deny 'stage' "the stage has no '$webRelativeDir'" }
if (-not [System.IO.File]::Exists($stageFfmpeg)) { Deny 'stage' "the stage has no '$ffmpegRelativeExe'" }

foreach ($stateDir in 'config', 'data', 'cache', 'log') {
    $candidate = [System.IO.Path]::Combine($stage, $stateDir)
    if ([System.IO.Directory]::Exists($candidate)) {
        Deny 'stage' ("the stage carries a '$stateDir' directory. Everything under %ProgramData% is " +
            'operator-owned (W0 §9.1) and a package that shipped it would overwrite an operator''s')
    }
}

# ---------------------------------------------------------------------------
# The harvested half of the payload: the accepted stage, minus the two files the
# authoring holds back. Materialised once and reused, and re-validated every
# time rather than trusted because it exists -- a harvest root left behind by a
# different stage would package a different server.
# ---------------------------------------------------------------------------
$WITHHELD = @($serverRelativeExe, 'tesserafin-server-service.ps1')

$harvest = [System.IO.Path]::GetFullPath($HarvestRoot)
if (-not [System.IO.Directory]::Exists($harvest)) {
    $null = [System.IO.Directory]::CreateDirectory($harvest)
    foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($stage)) {
        $name = [System.IO.Path]::GetFileName($entry)
        if ($WITHHELD -contains $name) { continue }
        $destination = [System.IO.Path]::Combine($harvest, $name)
        Copy-Item -LiteralPath $entry -Destination $destination -Recurse -Force
    }
}

foreach ($name in $WITHHELD) {
    if ([System.IO.File]::Exists([System.IO.Path]::Combine($harvest, $name))) {
        Deny 'harvest' ("the harvest root still holds '$name'. It would be delivered by the harvest " +
            'AND authored explicitly, which is two components claiming one file in one directory')
    }
}
if (-not [System.IO.Directory]::Exists([System.IO.Path]::Combine($harvest, $webRelativeDir))) {
    Deny 'harvest' "the harvest root has no '$webRelativeDir', so it is not the accepted stage"
}
if (-not [System.IO.File]::Exists([System.IO.Path]::Combine($harvest, $ffmpegRelativeExe))) {
    Deny 'harvest' "the harvest root has no '$ffmpegRelativeExe', so it is not the accepted stage"
}

# Every staged file is either harvested or deliberately withheld. A file that is
# neither would be silently dropped from the package.
$stagedNames = @([System.IO.Directory]::EnumerateFileSystemEntries($stage) |
    ForEach-Object { [System.IO.Path]::GetFileName($_) })
$harvestedNames = @([System.IO.Directory]::EnumerateFileSystemEntries($harvest) |
    ForEach-Object { [System.IO.Path]::GetFileName($_) })
$missing = @($stagedNames | Where-Object { $WITHHELD -notcontains $_ -and $harvestedNames -notcontains $_ })
if ($missing.Count -gt 0) {
    Deny 'harvest' ("the harvest root is missing top-level entries the stage has, and nothing withholds " +
        "them: $($missing -join ', ')")
}

# ---------------------------------------------------------------------------
# The version, from the one canonical source.
# ---------------------------------------------------------------------------
$sharedVersion = [System.IO.Path]::Combine($repo, 'SharedVersion.cs')
if (-not [System.IO.File]::Exists($sharedVersion)) { Deny 'version' "no canonical version source at '$sharedVersion'" }
$versionMatch = [regex]::Match([System.IO.File]::ReadAllText($sharedVersion), '\[assembly: ?AssemblyVersion\("([^"]*)"\)\]')
if (-not $versionMatch.Success) { Deny 'version' "no [assembly: AssemblyVersion(...)] in '$sharedVersion'" }
$version = $versionMatch.Groups[1].Value
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    Deny 'version' ("the declared version '$version' is not a MAJOR.MINOR.PATCH SemVer core, and a " +
        'Windows Installer ProductVersion cannot carry anything else')
}

# W4-A4 (#234): the same read, plus a bounded bump. `$declaredVersion` is what
# the commit says and is what every earlier W4 slice packages; `$version` is what
# THIS build states as its ProductVersion. They are the same string whenever
# -PatchBump is 0, which is every call this repository made before W4-A4.
$declaredVersion = $version
if ($PatchBump -ne 0) {
    $fields = $declaredVersion.Split('.')
    $version = '{0}.{1}.{2}' -f $fields[0], $fields[1], ([int]$fields[2] + $PatchBump)
    if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        Deny 'version' ("bumping the declared version '$declaredVersion' by $PatchBump produced " +
            "'$version', which is not a MAJOR.MINOR.PATCH SemVer core")
    }
    # A bump that did not RAISE the ProductVersion would build a package
    # MajorUpgrade cannot supersede, and the upgrade proof would then measure a
    # reinstall while reading like an upgrade.
    if ([version]$version -le [version]$declaredVersion) {
        Deny 'version' ("bumping '$declaredVersion' by $PatchBump produced '$version', which is not " +
            'higher, so MajorUpgrade would treat it as a reinstall or refuse it as a downgrade')
    }
}

# ---------------------------------------------------------------------------
# The toolset, pinned. `dotnet tool install` answers non-zero when the tool is
# already installed at that version, which is a success for this script's
# purposes -- what matters is the version `wix --version` then reports.
# ---------------------------------------------------------------------------
& dotnet tool install --global wix --version $WixVersion *> $null
$reportedRaw = (& wix --version 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { Deny 'toolset' "the pinned WiX toolset is not runnable: $($reportedRaw.Trim())" }
# The version line, not the whole stream. `wix` prefixes its output with a
# banner on any host it does not consider supported, and a StartsWith over the
# whole stream would then refuse a correctly pinned toolset.
$reported = @($reportedRaw -split "\r?\n" | Where-Object { $_.Trim() -match '^[0-9]+\.[0-9]+\.[0-9]+' }) |
    Select-Object -First 1
if (-not $reported) { Deny 'toolset' "wix reported no version: $($reportedRaw.Trim())" }
$reported = $reported.Trim()
if (-not $reported.StartsWith($WixVersion)) {
    Deny 'toolset' "asked for WiX $WixVersion and got '$reported'"
}

# ---------------------------------------------------------------------------
# The extension, pinned the same way and re-read the same way. W4-A2-R1 (#234)
# authorised `util:ServiceConfig` in place of the core element that reached
# MSI 1939, and this is the first WiX extension this packaging depends on.
#
# `wix extension add -g` is idempotent, and like `dotnet tool install` above it
# can answer non-zero for something that is already present -- so what is graded
# is not its exit code but the version `wix extension list -g` then reports. A
# pin nobody reads back is not a pin: an extension resolved to some other
# version would author a policy this script never asked for, and that would
# surface as a live service with the wrong recovery row rather than as a
# refusal here.
# ---------------------------------------------------------------------------
$utilExtension = "WixToolset.Util.wixext/$UtilExtensionVersion"
& wix extension add -g $utilExtension *> $null
$extensionsRaw = (& wix extension list -g 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    Deny 'extension' "could not list the installed WiX extensions: $($extensionsRaw.Trim())"
}
$extensionLine = @($extensionsRaw -split "\r?\n" |
    Where-Object { $_.Trim().StartsWith('WixToolset.Util.wixext', [System.StringComparison]::OrdinalIgnoreCase) } |
    Select-Object -First 1)
if (-not $extensionLine) {
    Deny 'extension' ("WixToolset.Util.wixext is not installed after asking for $utilExtension. " +
        "wix reported: $($extensionsRaw.Trim())")
}
$extensionLine = ([string]$extensionLine[0]).Trim()
if (-not $extensionLine.Contains($UtilExtensionVersion)) {
    Deny 'extension' "asked for $utilExtension and got '$extensionLine'"
}

$outDir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutPath))
$null = [System.IO.Directory]::CreateDirectory($outDir)
if ([System.IO.File]::Exists($OutPath)) { [System.IO.File]::Delete($OutPath) }

"W4-A0 build: version $version (declared $declaredVersion, patch bump $PatchBump), mutation $Mutation, WiX $reported"
"           extension $extensionLine"
"           stage  $stage"
"           output $OutPath"

$buildLog = & wix build -arch x64 `
    -ext $utilExtension `
    -d StageRoot="$stage" `
    -d HarvestRoot="$harvest" `
    -d Version="$version" `
    -d Mutation="$Mutation" `
    -o "$OutPath" "$authoring" 2>&1 | Out-String
$buildExit = $LASTEXITCODE
$buildLog.Trim()

if ($buildExit -ne 0) {
    if ([System.IO.File]::Exists($OutPath)) { [System.IO.File]::Delete($OutPath) }
    Deny 'wix' "wix build exited $buildExit"
}
if (-not [System.IO.File]::Exists($OutPath)) { Deny 'wix' 'wix build exited 0 and wrote no MSI' }

$digest = (Get-FileHash -LiteralPath $OutPath -Algorithm SHA256).Hash.ToLowerInvariant()
"W4-A0 built $([System.IO.Path]::GetFileName($OutPath)) sha256 $digest"
exit 0
