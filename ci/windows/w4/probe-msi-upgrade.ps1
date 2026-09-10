#Requires -Version 7.2
<#
.SYNOPSIS
    W4-A4 (#234). Exercise `MajorUpgrade`: install package A, write sentinels
    into the four `%ProgramData%` state directories, install package B over it,
    and read back what the upgrade did to the binaries, to the state, to the
    service and to the W0 §9.3 descriptors.

.DESCRIPTION
    `MajorUpgrade` has stood in `packaging/windows/msi/Tesserafin.wxs` since
    W4-A0 and no slice has ever exercised it. W4-A4 does exactly that and claims
    exactly that.

    THE PAIR

    A and B are built from THIS authoring, through the SAME builder, from ONE
    accepted W2 package. They differ in two ways and no others:

      * B's `Version` is the version this commit declares with its PATCH field
        raised by one. It comes from the same `SharedVersion.cs` read the
        builder has always done -- see `-PatchBump` in `build-msi.ps1`.
        `SharedVersion.cs` is not edited, on this branch or anywhere;

      * A's `tesserafin.exe` carries a marker and B's does not.

    The marker is what makes "the binaries are B's" a measurable statement at
    all. Both packages are built from one accepted stage, so without it their
    executables would be the same bytes and the predicate would be true of an
    upgrade that delivered nothing. A is the package that gets marked, so that
    what is left on the machine at the end of the real pair is the accepted
    package's own bytes and the reading is "the marker is gone and the accepted
    executable is there" rather than "a fixture is there".

    The marker is appended to a COPY of the accepted executable and it is
    appended, not spliced: the file stays a well-formed PE whose headers,
    sections and entry point are the accepted ones. Nothing in this slice runs
    it -- W0 §10 leaves the service stopped and the W4-A4 ruling is explicit
    that starting it is not this slice -- so what is measured about it is its
    digest, which is the whole reason it exists.

    THE FIVE HOSTILE CONTROLS

    Three are LIVE pairs -- a real A, a real deliberately broken B, a real
    msiexec upgrade and a real read-back:

      upgrade-same-exe      B is built from the stage A was built from, so the
                            upgrade redelivers A's bytes.
      upgrade-wipes-state   B empties the four state directories on install.
      upgrade-no-service    B registers no service at all.

    Two are TABLE controls. The package is really built from this authoring and
    its own tables are really read, one cell is changed in a COPY, and it is
    deliberately never installed:

      upgrade-upgradecode   B's `Property`/`UpgradeCode` is a different GUID.
      upgrade-starts-service  B's `ServiceControl`/`Event` carries the
                            start-on-install bit.

    Both would be graded by an outcome that is NOT the defect if they were
    installed. A B with a different UpgradeCode does not upgrade A -- it
    installs beside it, which is the second product the W4-A4 ruling forbids
    inventing, and it would then fight A for the service name. A B that starts
    the service inside the transaction is the 1920-to-1603 rollback W0 §5.2
    measured: the transaction would roll back, A would still be installed, the
    service would be Stopped, and the live reading would be GREEN for a package
    that asked for the forbidden thing. Both are graded from the tables, both
    are recorded in the evidence as table-derived, and the probe refuses if
    either edit did not take -- a control that cannot fire is not a control.

.PARAMETER RepoRoot
    The checkout being packaged.

.PARAMETER WorkDir
    Scratch. The package is assembled here and the MSIs are built here.

.PARAMETER InstallPrefix
    The root of the disposable INSTALLFOLDER prefixes. Each pair installs BOTH
    of its packages under the same subdirectory of this, which is what makes the
    upgrade an upgrade of the same installation.

    Only INSTALLFOLDER is redirected. DATAFOLDER is NOT, exactly as W4-A3 has
    it: W0 §4's argument list names `%ProgramData%\Tesserafin\Server` and a run
    that redirected it would measure a layout no operator will ever get.

.PARAMETER EvidencePath
    Where the evidence document is written.

.PARAMETER SourceDateEpoch
    Passed to the frozen W2-A2 assembler. The committer time of the commit being
    built; never the clock.

.PARAMETER OrasPath
    The pinned ORAS client the frozen FFmpeg consumer needs.

.PARAMETER PythonPath
    The interpreter the frozen assembler uses.

.PARAMETER HeadSha
    Recorded in the evidence so a reviewer can see which commit was packaged.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $RepoRoot,
    [Parameter(Mandatory = $true)] [string] $WorkDir,
    [Parameter(Mandatory = $true)] [string] $InstallPrefix,
    [Parameter(Mandatory = $true)] [string] $EvidencePath,
    [Parameter(Mandatory = $true)] [int64]  $SourceDateEpoch,
    [Parameter(Mandatory = $true)] [string] $OrasPath,
    [Parameter()] [string] $PythonPath,
    [Parameter(Mandatory = $true)] [string] $HeadSha
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$SERVICE_NAME = 'Tesserafin'
$SERVICE_KEY = "HKLM:\SYSTEM\CurrentControlSet\Services\$SERVICE_NAME"
$RETAINED_STATE_KEY = 'HKLM:\SOFTWARE\Tesserafin'
$FROZEN_UPGRADE_CODE = '0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05'
# Any GUID that is not the frozen one. It is a control input and never reaches
# the authoring, which `ci/windows/w4/msi-controls.py` still holds to exactly
# one UpgradeCode attribute carrying exactly the frozen value.
$CONTROL_UPGRADE_CODE = '6b1e8d37-5f92-4a04-8e7c-3d05b9f2a618'

# The pairs, in the order they run. `none` first: a run that cannot install the
# real package twice has nothing to say about a broken one.
$CONTROLS = @('none', 'upgrade-same-exe', 'upgrade-wipes-state', 'upgrade-no-service',
    'upgrade-upgradecode', 'upgrade-starts-service')
$TABLE_CONTROLS = @('upgrade-upgradecode', 'upgrade-starts-service')

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, 'W4MsiAssertions.psm1')) -Force
Import-Module ([System.IO.Path]::Combine($PSScriptRoot, 'W4MsiInstruments.psm1')) -Force

$evidence = [ordered]@{
    slice = 'W4-A4'
    tracker = 234
    headSha = $HeadSha
    # Stated as data so the closing report cannot claim more than the run did.
    signed = $false
    published = $false
    startedTheService = $false
    appliedAcls = $true
    exercisedMajorUpgrade = $true
    # The W4-A4 ruling's "not this slice" list, restated as data.
    exercisedRepair = $false
    installedASecondProduct = $false
    editedSharedVersion = $false
    reproducibilityClaim = 'none -- W0 §5.6 already measured that MSI bytes are not bit-for-bit'
    packages = [ordered]@{}
    runs = [ordered]@{}
}

function Save-Evidence {
    $dir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($EvidencePath))
    $null = [System.IO.Directory]::CreateDirectory($dir)
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $EvidencePath -Encoding utf8NoBOM
}

function Deny {
    param([Parameter(Mandatory = $true)] [string] $Reason,
          [Parameter(Mandatory = $true)] [string] $Detail)
    $evidence.refusal = [ordered]@{ reason = $Reason; detail = $Detail }
    Save-Evidence
    Write-Host "W4-A4 REFUSED [$Reason]: $Detail"
    exit 1
}

function Write-Note { param([string] $Text) Write-Host "W4-A4 :: $Text" }

function Get-Sha256 {
    param([Parameter(Mandatory = $true)] [string] $Path)
    if (-not [System.IO.File]::Exists($Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---------------------------------------------------------------------------
# Preconditions
# ---------------------------------------------------------------------------
if (-not $IsWindows) { Deny 'platform' 'this proof only means anything on a native Windows host' }
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Deny 'platform' "this job needs PowerShell 7 or newer; it has $($PSVersionTable.PSVersion)"
}
$identity = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $identity.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Deny 'privilege' 'a per-machine MSI cannot be installed without elevation'
}

if (Test-Path -LiteralPath $SERVICE_KEY) {
    if (-not (Remove-W4ServiceIfPresent -ServiceName $SERVICE_NAME -ServiceKey $SERVICE_KEY)) {
        Deny 'precondition' ("a service named '$SERVICE_NAME' is already registered on this host and " +
            'could not be removed, so nothing this run observed about it would be attributable')
    }
}

$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$work = [System.IO.Path]::GetFullPath($WorkDir)
if ([System.IO.Directory]::Exists($work) -and
    @([System.IO.Directory]::EnumerateFileSystemEntries($work)).Count -gt 0) {
    Deny 'work-dir' "'$work' is not empty"
}
$null = [System.IO.Directory]::CreateDirectory($work)

$prefixRoot = [System.IO.Path]::GetFullPath($InstallPrefix)
$programFilesTesserafin = [System.IO.Path]::Combine($env:ProgramFiles, 'Tesserafin')
if ([System.IO.Directory]::Exists($programFilesTesserafin)) {
    Deny 'precondition' ("'$programFilesTesserafin' already exists, so this run could not tell a real " +
        '%ProgramFiles% install apart from something that was already there')
}

$programDataRoot = [System.IO.Path]::Combine($env:ProgramData, 'Tesserafin', 'Server')
$programDataTesserafin = [System.IO.Path]::Combine($env:ProgramData, 'Tesserafin')

function Reset-InstalledState {
    <#
        Put the machine back to "this package has never been installed",
        BETWEEN pairs and before the first one. Never between A and B: that is
        the sequence under test.

        Both removals are load-bearing, for the reasons W4-A3 recorded.
        `%ProgramData%\Tesserafin` is where the descriptor under test lives, and
        SetNamedSecurityInfo leaves the protection flag alone unless told
        otherwise. `HKLM\SOFTWARE\Tesserafin` is the retained-state key path:
        those components are NeverOverwrite, so with the key still present the
        installer SKIPS them and the state directories are never recreated.
    #>
    Remove-Item -LiteralPath $programDataTesserafin -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $RETAINED_STATE_KEY -Recurse -Force -ErrorAction SilentlyContinue
    return (-not [System.IO.Directory]::Exists($programDataTesserafin)) -and
        (-not (Test-Path -LiteralPath $RETAINED_STATE_KEY))
}

# ---------------------------------------------------------------------------
# The accepted layout, read from the script that registers the service for the
# portable ZIP -- never restated here.
# ---------------------------------------------------------------------------
$acceptedScript = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'w2', 'tesserafin-server-service.ps1')
if (-not [System.IO.File]::Exists($acceptedScript)) {
    Deny 'layout' "no accepted W2-A5 service script at '$acceptedScript'"
}
$acceptedText = [System.IO.File]::ReadAllText($acceptedScript)
function Get-AcceptedConstant {
    param([Parameter(Mandatory = $true)] [string] $Name)
    $match = [regex]::Match($acceptedText, "(?m)^\`$$Name\s*=\s*'([^']+)'\s*$")
    if (-not $match.Success) { Deny 'layout' "the accepted W2-A5 service script does not define `$$Name" }
    return $match.Groups[1].Value
}
$serverRelativeExe = (Get-AcceptedConstant 'SERVER_RELATIVE_EXE') -replace '/', '\'
$webRelativeDir = (Get-AcceptedConstant 'WEB_RELATIVE_DIR') -replace '/', '\'
$ffmpegRelativeExe = (Get-AcceptedConstant 'FFMPEG_RELATIVE_EXE') -replace '/', '\'

# ---------------------------------------------------------------------------
# 1. The package, from the frozen W2-A2 assembler
# ---------------------------------------------------------------------------
$assembler = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'w2', 'assemble-server-zip.ps1')
if (-not [System.IO.File]::Exists($assembler)) { Deny 'prerequisite' "no assembler at '$assembler'" }

$assemblyOut = [System.IO.Path]::Combine($work, 'assembly', 'out')
$assemblerArguments = @{
    RepoRoot = $repo
    WorkDir = [System.IO.Path]::Combine($work, 'assembly', 'work')
    OutDir = $assemblyOut
    SourceDateEpoch = $SourceDateEpoch
    OrasPath = $OrasPath
}
if ($PythonPath) { $assemblerArguments['PythonPath'] = $PythonPath }
& $assembler @assemblerArguments
if ($LASTEXITCODE -ne 0) { Deny 'assembly' 'the frozen W2-A2 assembler produced no archive' }

$archives = @(Get-ChildItem -LiteralPath $assemblyOut -Filter '*.zip' -File)
if ($archives.Count -ne 1) { Deny 'assembly' "the assembler wrote $($archives.Count) archives" }
$archive = $archives[0]

$extractRoot = [System.IO.Path]::Combine($work, 'pkg')
$null = [System.IO.Directory]::CreateDirectory($extractRoot)
[System.IO.Compression.ZipFile]::ExtractToDirectory($archive.FullName, $extractRoot)
$tops = @(Get-ChildItem -LiteralPath $extractRoot -Force)
if ($tops.Count -ne 1 -or -not $tops[0].PSIsContainer) {
    Deny 'top-level' "the archive extracted $($tops.Count) top-level entries"
}
$stageRoot = $tops[0].FullName
$stagedExePath = [System.IO.Path]::Combine($stageRoot, $serverRelativeExe)
if (-not [System.IO.File]::Exists($stagedExePath)) {
    Deny 'stage' "the accepted stage has no '$serverRelativeExe'"
}

# ---------------------------------------------------------------------------
# 2. The two executables
#
# The accepted one, kept aside so the stage can be put back exactly as the
# assembler produced it, and a marked COPY for A.
#
# The marker is appended. The file stays a well-formed PE -- headers, sections
# and entry point are the accepted ones and none of them moves -- and nothing in
# this slice runs it: the W4-A4 ruling does not start the service and W0 §10
# does not either. What is measured about this file is its DIGEST, which is what
# makes "the upgrade replaced the binaries" a statement about bytes rather than
# about a timestamp.
# ---------------------------------------------------------------------------
$pristineExePath = [System.IO.Path]::Combine($work, 'tesserafin.accepted.exe')
Copy-Item -LiteralPath $stagedExePath -Destination $pristineExePath -Force
$pristineExeSha = Get-Sha256 -Path $pristineExePath

$markedExePath = [System.IO.Path]::Combine($work, 'tesserafin.marked.exe')
Copy-Item -LiteralPath $stagedExePath -Destination $markedExePath -Force
$marker = [System.Text.Encoding]::UTF8.GetBytes(
    "`nW4-A4 upgrade fixture, tracker 234, commit $HeadSha. Not a shippable binary.`n")
$markedStream = [System.IO.File]::Open($markedExePath, [System.IO.FileMode]::Append,
    [System.IO.FileAccess]::Write)
try { $markedStream.Write($marker, 0, $marker.Length) } finally { $markedStream.Dispose() }
$markedExeSha = Get-Sha256 -Path $markedExePath

if ($markedExeSha -ceq $pristineExeSha) {
    Deny 'fixture' ('the marked executable hashes to the accepted one, so "the upgrade replaced the ' +
        'binaries" would be true of an upgrade that replaced nothing')
}

function Set-StageExecutable {
    param([Parameter(Mandatory = $true)] [string] $Source)
    Copy-Item -LiteralPath $Source -Destination $stagedExePath -Force
    # The mtime is set forward deliberately. `Copy-Item` preserves the source's
    # LastWriteTime, and a stage whose executable looks older than the package
    # that was built from it is the shape that makes a later build reuse a stale
    # input without saying so.
    (Get-Item -LiteralPath $stagedExePath).LastWriteTimeUtc = [DateTime]::UtcNow
    $actual = Get-Sha256 -Path $stagedExePath
    $expected = Get-Sha256 -Path $Source
    if ($actual -cne $expected) {
        Deny 'fixture' "staging '$Source' left a different file behind ($actual, expected $expected)"
    }
}

$evidence.packages.acceptedExeSha256 = $pristineExeSha
$evidence.packages.markedExeSha256 = $markedExeSha
$evidence.packages.markerBytes = $marker.Length
$evidence.packages.archiveName = $archive.Name
$evidence.packages.archiveSha256 = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$evidence.packages.stagedFileCount = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File).Count
Write-Note "package $($archive.Name), $($evidence.packages.stagedFileCount) staged files"
Write-Note "accepted exe $pristineExeSha"
Write-Note "marked   exe $markedExeSha  (+$($marker.Length) bytes, control fixture for A)"
Save-Evidence

# ---------------------------------------------------------------------------
# 3. The packages
#
# One harvest root for all five builds. The harvest deliberately EXCLUDES
# `tesserafin.exe` -- the authoring delivers it explicitly, from StageRoot --
# so swapping the staged executable between builds does not invalidate it, and
# the builder re-validates it on every call regardless.
# ---------------------------------------------------------------------------
$builder = [System.IO.Path]::Combine($PSScriptRoot, 'build-msi.ps1')
$harvestRoot = [System.IO.Path]::Combine($work, 'harvest')
$logDir = [System.IO.Path]::Combine($work, 'logs')
$null = [System.IO.Directory]::CreateDirectory($logDir)

function Build-Package {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $Mutation,
        [Parameter(Mandatory = $true)] [int] $PatchBump
    )
    $msiPath = [System.IO.Path]::Combine($work, "$Name.msi")
    & $builder -RepoRoot $repo -StageRoot $stageRoot -HarvestRoot $harvestRoot `
        -OutPath $msiPath -Mutation $Mutation -PatchBump $PatchBump
    if ($LASTEXITCODE -ne 0 -or -not [System.IO.File]::Exists($msiPath)) {
        Deny 'build' "the MSI '$Name' (mutation '$Mutation', patch bump $PatchBump) was not built"
    }
    return $msiPath
}

# The pristine stage first, so that every B built from the accepted executable
# is built before the stage is ever touched.
Set-StageExecutable -Source $pristineExePath
$msi = @{}
$msi['b-none'] = Build-Package -Name 'b-none' -Mutation 'none' -PatchBump 1
$msi['b-wipes-state'] = Build-Package -Name 'b-wipes-state' -Mutation 'upgrade-wipes-state' -PatchBump 1
$msi['b-no-service'] = Build-Package -Name 'b-no-service' -Mutation 'upgrade-no-service' -PatchBump 1

# Then the marked stage: A, and the one B that is deliberately built from it.
Set-StageExecutable -Source $markedExePath
$msi['a'] = Build-Package -Name 'a' -Mutation 'none' -PatchBump 0
$msi['b-same-exe'] = Build-Package -Name 'b-same-exe' -Mutation 'none' -PatchBump 1

# And back, so nothing after this point can read a marked stage by accident.
Set-StageExecutable -Source $pristineExePath

# ── the two table controls ──────────────────────────────────────────────────
# Copies of the REAL B. One cell each, changed through Windows Installer, and
# re-read afterwards: an edit that silently did not take would leave a control
# that grades green while proving nothing.
$msi['b-upgradecode'] = [System.IO.Path]::Combine($work, 'b-upgradecode.msi')
Copy-Item -LiteralPath $msi['b-none'] -Destination $msi['b-upgradecode'] -Force
Set-W4MsiCell -MsiPath $msi['b-upgradecode'] -Column 2 `
    -StringValue "{$($CONTROL_UPGRADE_CODE.ToUpperInvariant())}" `
    -Query "SELECT ``Property``, ``Value`` FROM ``Property`` WHERE ``Property`` = 'UpgradeCode'"
$controlUpgradeCodeReadBack = Get-W4NormalisedGuid -Value (
    Get-W4MsiProperty -MsiPath $msi['b-upgradecode'] -Name 'UpgradeCode')
if ($controlUpgradeCodeReadBack -ceq $FROZEN_UPGRADE_CODE) {
    Deny 'control' ("the 'upgrade-upgradecode' control still carries the frozen UpgradeCode after the " +
        'edit, so the control could never have fired')
}

$msi['b-starts-service'] = [System.IO.Path]::Combine($work, 'b-starts-service.msi')
Copy-Item -LiteralPath $msi['b-none'] -Destination $msi['b-starts-service'] -Force
$realEvent = Get-W4MsiServiceControlEvent -MsiPath $msi['b-none'] -ServiceName $SERVICE_NAME
if ($null -eq $realEvent) {
    Deny 'control' ("the real package carries no ServiceControl row for '$SERVICE_NAME', so the " +
        "'upgrade-starts-service' control has nothing to change")
}
# One bit ORed into what the package really has, so the control differs from the
# real package in exactly the thing it is a control for.
Set-W4MsiCell -MsiPath $msi['b-starts-service'] -Column 3 -IntegerValue ($realEvent.event -bor 1) `
    -Query ("SELECT ``ServiceControl``, ``Name``, ``Event`` FROM ``ServiceControl`` " +
        "WHERE ``ServiceControl`` = '$($realEvent.key)'")
if (-not (Get-W4MsiStartsServiceOnInstall -MsiPath $msi['b-starts-service'] -ServiceName $SERVICE_NAME)) {
    Deny 'control' ("the 'upgrade-starts-service' control does not carry the start-on-install bit " +
        'after the edit, so the control could never have fired')
}
if (Get-W4MsiStartsServiceOnInstall -MsiPath $msi['b-none'] -ServiceName $SERVICE_NAME) {
    Deny 'authoring' ('the REAL package asks the installer to start the service inside the ' +
        'transaction. W0 §5.2 measured that failing with 1920 and rolling the install back to 1603')
}

function Get-PackageFacts {
    <#
        A package's identity, read out of its own tables, plus the digest of the
        executable it was built from -- which the caller knows and the tables do
        not. This is the `aMsi` / `bMsi` half of the grader's observation.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $MsiPath,
        [Parameter(Mandatory = $true)] [string] $StagedExeSha256
    )
    return @{
        productCode = Get-W4MsiProperty -MsiPath $MsiPath -Name 'ProductCode'
        productVersion = Get-W4MsiProperty -MsiPath $MsiPath -Name 'ProductVersion'
        upgradeCode = Get-W4MsiProperty -MsiPath $MsiPath -Name 'UpgradeCode'
        stagedExeSha256 = $StagedExeSha256
        startsServiceOnInstall = (Get-W4MsiStartsServiceOnInstall -MsiPath $MsiPath -ServiceName $SERVICE_NAME)
        sha256 = Get-Sha256 -Path $MsiPath
        bytes = (Get-Item -LiteralPath $MsiPath).Length
        fileCount = @(Get-W4MsiFileNames -MsiPath $MsiPath).Count
    }
}

# A is the same package in every pair, so its facts are read once.
$aFacts = Get-PackageFacts -MsiPath $msi['a'] -StagedExeSha256 $markedExeSha

# Which B each control installs, and which staged executable that B was built
# from. `upgrade-same-exe` is the one whose B was built from A's stage.
$bForControl = [ordered]@{
    'none' = @{ msi = 'b-none'; exe = $pristineExeSha }
    'upgrade-same-exe' = @{ msi = 'b-same-exe'; exe = $markedExeSha }
    'upgrade-wipes-state' = @{ msi = 'b-wipes-state'; exe = $pristineExeSha }
    'upgrade-no-service' = @{ msi = 'b-no-service'; exe = $pristineExeSha }
    'upgrade-upgradecode' = @{ msi = 'b-upgradecode'; exe = $pristineExeSha }
    'upgrade-starts-service' = @{ msi = 'b-starts-service'; exe = $pristineExeSha }
}

$evidence.packages.a = $aFacts
$evidence.packages.b = [ordered]@{}
foreach ($control in $CONTROLS) {
    $evidence.packages.b[$control] = Get-PackageFacts `
        -MsiPath $msi[$bForControl[$control].msi] -StagedExeSha256 $bForControl[$control].exe
}
Save-Evidence

Write-Note ("A  version $($aFacts.productVersion)  product $($aFacts.productCode)  " +
    "upgrade $($aFacts.upgradeCode)")
Write-Note ("B  version $($evidence.packages.b['none'].productVersion)  " +
    "product $($evidence.packages.b['none'].productCode)  " +
    "upgrade $($evidence.packages.b['none'].upgradeCode)")

# ---------------------------------------------------------------------------
# 4. The state observation
# ---------------------------------------------------------------------------
$SENTINEL_NAME = 'w4a4.sentinel'

function Write-Sentinels {
    <#
        One sentinel per state directory, with identical content, written
        BETWEEN the two installs. Identical on purpose: the grader compares each
        directory's sentinel against one expected digest, so a directory whose
        file was deleted and recreated by the upgrade is distinguishable from
        one whose file was left alone.
    #>
    param([Parameter(Mandatory = $true)] [string] $Text)
    $written = 0
    foreach ($name in 'config', 'data', 'cache', 'log') {
        $directory = [System.IO.Path]::Combine($programDataRoot, $name)
        if (-not [System.IO.Directory]::Exists($directory)) { continue }
        Set-Content -LiteralPath ([System.IO.Path]::Combine($directory, $SENTINEL_NAME)) `
            -Value $Text -Encoding utf8NoBOM
        $written++
    }
    return $written
}

function Get-StateObservation {
    $observation = @{}
    foreach ($name in 'config', 'data', 'cache', 'log') {
        $directory = [System.IO.Path]::Combine($programDataRoot, $name)
        $sentinel = [System.IO.Path]::Combine($directory, $SENTINEL_NAME)
        $observation[$name] = @{
            directory = [System.IO.Directory]::Exists($directory)
            sentinel = [System.IO.File]::Exists($sentinel)
            sha256 = Get-Sha256 -Path $sentinel
        }
    }
    return $observation
}

function Show-StateObservation {
    param([Parameter(Mandatory = $true)] $Observation, [Parameter(Mandatory = $true)] [string] $Label)
    Write-Host "W4-A4 :: state directories $Label"
    foreach ($name in 'config', 'data', 'cache', 'log') {
        $entry = $Observation[$name]
        Write-Host ("  {0,-8} directory {1,-5}  sentinel {2,-5}  {3}" -f `
            $name, $entry.directory, $entry.sentinel,
            $(if ($entry.sha256) { $entry.sha256 } else { '(none)' }))
    }
}

# ---------------------------------------------------------------------------
# 5. One pair, one verdict -- six times
# ---------------------------------------------------------------------------
if (-not (Reset-InstalledState)) {
    Deny 'precondition' ("'$programDataTesserafin' or '$RETAINED_STATE_KEY' is present on this host " +
        'and could not be removed, so the first pair would have measured state this run did not create')
}

$allPassed = $true

foreach ($control in $CONTROLS) {
    Write-Note "=== $control"
    $isTableControl = ($TABLE_CONTROLS -contains $control)
    $run = [ordered]@{ control = $control; tableDerived = $isTableControl }
    $bFacts = $evidence.packages.b[$control]

    # A TABLE control installs nothing. It is graded on the REAL pair's live
    # observation with only the facts read out of ITS OWN package substituted,
    # which is exactly what the defect changes and nothing else. The live half
    # of that observation was measured, on this host, in the `none` pair above.
    if ($isTableControl) {
        if (-not $evidence.runs.Contains('none')) {
            Deny 'order' "the table control '$control' ran before the real pair it is graded against"
        }
        $observation = $script:realPairObservation.Clone()
        $observation['bMsi'] = @{
            productCode = $bFacts.productCode
            productVersion = $bFacts.productVersion
            upgradeCode = $bFacts.upgradeCode
            stagedExeSha256 = $bFacts.stagedExeSha256
            startsServiceOnInstall = $bFacts.startsServiceOnInstall
        }
        $predicates = Get-W4UpgradePredicates -Observation $observation
        $verdict = Get-W4UpgradeVerdict -Predicates $predicates -Control $control
        $run.installedAnything = $false
        $run.notInstalledBecause = $(if ($control -eq 'upgrade-upgradecode') {
            'a package with a different UpgradeCode installs BESIDE the first one, which is the ' +
            'second product the W4-A4 ruling forbids inventing'
        } else {
            'a package that starts the service inside the transaction is the 1920-to-1603 rollback ' +
            'W0 §5.2 measured, which would leave A installed and grade this control on the rollback'
        })
        $run.predicates = $predicates
        $run.verdict = $verdict
        $evidence.runs[$control] = $run
        Save-Evidence
        Write-Note "$control -> $(if ($verdict.passed) { 'PASS' } else { 'FAIL' }): $($verdict.detail)"
        if (-not $verdict.passed) { $allPassed = $false }
        continue
    }

    # ── a LIVE pair ─────────────────────────────────────────────────────────
    $prefix = [System.IO.Path]::Combine($prefixRoot, $control)
    $bMsiPath = $msi[$bForControl[$control].msi]

    # A. The same real package in every pair, and the only install this probe
    # refuses on: a pair whose FIRST package would not install has nothing to
    # say about what the second one did to it.
    $run.installAExit = Invoke-W4Msi -Arguments @('/i', "`"$($msi['a'])`"", "INSTALLFOLDER=`"$prefix`"") `
        -LogPath ([System.IO.Path]::Combine($logDir, "install-a-$control.log")) `
        -Label "install of A for '$control'"
    if ($run.installAExit -ne 0) {
        Deny 'install' ("msiexec /i exited $($run.installAExit) installing A for '$control'. Every pair " +
            'in this slice is a pair over an INSTALLED first package')
    }

    $run.installedExeAfterA = Get-Sha256 -Path ([System.IO.Path]::Combine($prefix, $serverRelativeExe))
    $run.serviceStateAfterA = Get-W4ServiceState -ServiceName $SERVICE_NAME
    $run.stateAfterA = Get-StateObservation
    $run.aProductStateAfterA = Get-W4ProductInstallState -ProductCode $aFacts.productCode
    if ($run.installedExeAfterA -cne $markedExeSha) {
        Deny 'install' ("A delivered '$($run.installedExeAfterA)' rather than the marked fixture " +
            "'$markedExeSha', so the pair could not tell A's binaries from B's")
    }

    # The sentinels, written between the two installs. This is the whole point
    # of the slice: state that exists BEFORE the upgrade and is looked for after.
    $sentinelText = "w4a4 $control $HeadSha"
    $run.sentinelsWritten = Write-Sentinels -Text $sentinelText
    if ($run.sentinelsWritten -ne 4) {
        Deny 'sentinel' ("A created $($run.sentinelsWritten) of the four state directories, so " +
            '"the upgrade kept them" would be a statement about directories that were never there')
    }
    $run.sentinelSha256 = Get-Sha256 -Path (
        [System.IO.Path]::Combine($programDataRoot, 'config', $SENTINEL_NAME))
    Show-StateObservation -Observation (Get-StateObservation) -Label "after A, sentinels written ($control)"

    # B, over A, into the SAME prefix. INSTALLFOLDER is passed again explicitly:
    # this authoring carries no remember-property, so an upgrade that omitted it
    # would install to the default location. See the W4-A4 document.
    $run.upgradeExit = Invoke-W4Msi -Arguments @('/i', "`"$bMsiPath`"", "INSTALLFOLDER=`"$prefix`"") `
        -LogPath ([System.IO.Path]::Combine($logDir, "install-b-$control.log")) `
        -Label "upgrade to B for '$control'"

    $service = Get-W4ServiceRegistry -ServiceKey $SERVICE_KEY
    $acls = Get-W4AclObservations -InstallPrefix $prefix -DataRoot $programDataTesserafin `
        -StateRoot $programDataRoot
    Show-W4AclObservations -Observations $acls -Label "after the upgrade ($control)"
    $stateAfter = Get-StateObservation
    Show-StateObservation -Observation $stateAfter -Label "after the upgrade ($control)"

    # Read once, recorded whole, and graded on `installed` alone. `state` and
    # `error` come along so that a red on either product predicate can be
    # diagnosed from the evidence rather than re-run.
    $aProductState = Get-W4ProductInstallState -ProductCode $aFacts.productCode
    $bProductState = Get-W4ProductInstallState -ProductCode $bFacts.productCode

    $observation = @{
        aMsi = $aFacts
        bMsi = @{
            productCode = $bFacts.productCode
            productVersion = $bFacts.productVersion
            upgradeCode = $bFacts.upgradeCode
            stagedExeSha256 = $bFacts.stagedExeSha256
            startsServiceOnInstall = $bFacts.startsServiceOnInstall
        }
        upgradeExit = $run.upgradeExit
        installPrefix = $prefix
        programDataRoot = $programDataRoot
        serverRelativeExe = $serverRelativeExe
        webRelativeDir = $webRelativeDir
        ffmpegRelativeExe = $ffmpegRelativeExe
        installedExeSha256 = (Get-Sha256 -Path ([System.IO.Path]::Combine($prefix, $serverRelativeExe)))
        installedServerExe = [System.IO.File]::Exists([System.IO.Path]::Combine($prefix, $serverRelativeExe))
        installedWebDir = [System.IO.Directory]::Exists([System.IO.Path]::Combine($prefix, $webRelativeDir))
        installedFfmpegExe = [System.IO.File]::Exists([System.IO.Path]::Combine($prefix, $ffmpegRelativeExe))
        service = $service
        serviceState = (Get-W4ServiceState -ServiceName $SERVICE_NAME)
        failureActions = (Get-W4ServiceFailureActions -ServiceName $SERVICE_NAME -ServiceKey $SERVICE_KEY)
        acls = $acls
        stateAfterUpgrade = $stateAfter
        sentinelSha256 = $run.sentinelSha256
        aProductInstalled = $aProductState.installed
        bProductInstalled = $bProductState.installed
    }

    $run.installedAnything = $true
    $run.serviceImagePath = $(if ($null -eq $service) { $null } else { $service.ImagePath })
    $run.serviceState = $observation.serviceState
    $run.installedExeAfterB = $observation.installedExeSha256
    $run.installedFileCount = $(if ([System.IO.Directory]::Exists($prefix)) {
        @(Get-ChildItem -LiteralPath $prefix -Recurse -File -Force).Count } else { 0 })
    $run.programFilesTesserafinExists = [System.IO.Directory]::Exists($programFilesTesserafin)
    $run.failureActions = $observation.failureActions
    $run.failureActionsEvidence = Get-W4ServiceFailureEvidence -ServiceName $SERVICE_NAME -ServiceKey $SERVICE_KEY
    $run.acls = $acls
    $run.stateAfterUpgrade = $stateAfter
    $run.aProductState = $aProductState
    $run.bProductState = $bProductState
    $run.aProductInstalled = $observation.aProductInstalled
    $run.bProductInstalled = $observation.bProductInstalled

    $predicates = Get-W4UpgradePredicates -Observation $observation
    $verdict = Get-W4UpgradeVerdict -Predicates $predicates -Control $control
    $run.predicates = $predicates
    $run.verdict = $verdict

    # The real pair's observation is what the two table controls are graded
    # against, with only their own package's facts substituted. Kept here, after
    # it has been measured, so a table control can never be graded against
    # something no machine produced.
    if ($control -eq 'none') { $script:realPairObservation = $observation }

    # W4-A4 ruling: "downgrade (MajorUpgrade already refuses; record the 1638 if
    # you measure it, do not invent a second product)". Measured once, on the
    # real pair only, and RECORDED -- no predicate is graded on it, because the
    # ruling puts downgrade outside this slice.
    if ($control -eq 'none') {
        $run.downgradeExit = Invoke-W4Msi -Arguments @('/i', "`"$($msi['a'])`"", "INSTALLFOLDER=`"$prefix`"") `
            -LogPath ([System.IO.Path]::Combine($logDir, 'downgrade-a-over-b.log')) `
            -Label 'record-only downgrade: A over B'
        $run.downgradeNote = ('recorded, never graded. 1638 is "another version of this product is ' +
            'already installed". The W4-A4 ruling puts downgrade outside this slice')
        Write-Note "downgrade A over B exited $($run.downgradeExit) (recorded, not graded)"
        $run.serviceStateAfterDowngradeAttempt = Get-W4ServiceState -ServiceName $SERVICE_NAME
        $run.installedExeAfterDowngradeAttempt = Get-Sha256 -Path (
            [System.IO.Path]::Combine($prefix, $serverRelativeExe))
    }

    $evidence.runs[$control] = $run
    Save-Evidence
    Write-Note "$control -> $(if ($verdict.passed) { 'PASS' } else { 'FAIL' }): $($verdict.detail)"
    if (-not $verdict.passed) { $allPassed = $false }

    # Between pairs, and never between A and B.
    $run.uninstallExit = Invoke-W4Msi -Arguments @('/x', "`"$bMsiPath`"") `
        -LogPath ([System.IO.Path]::Combine($logDir, "uninstall-$control.log")) `
        -Label "uninstall after '$control'"
    if (-not (Remove-W4ServiceIfPresent -ServiceName $SERVICE_NAME -ServiceKey $SERVICE_KEY)) {
        Deny 'cleanup' ("the '$SERVICE_NAME' service survived pair '$control' and could not be removed, " +
            'so the next pair would have measured this one''s leftovers')
    }
    Remove-Item -LiteralPath $prefix -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Reset-InstalledState)) {
        Deny 'cleanup' ("'$programDataTesserafin' survived pair '$control' and could not be removed, " +
            'so the next pair would have measured this one''s security descriptor')
    }
    $evidence.runs[$control] = $run
    Save-Evidence
}

# Two controls with identical failure lists is the tell that the harness graded
# nothing at all, and it is invisible from a table of passes.
$redSets = @{}
foreach ($control in $CONTROLS) { $redSets[($evidence.runs[$control].verdict.red -join '|')] = $true }
$evidence.distinctRedSets = $redSets.Count
if ($redSets.Count -ne $CONTROLS.Count) {
    $allPassed = $false
    $evidence.inertHarness = "only $($redSets.Count) distinct red sets across $($CONTROLS.Count) pairs"
}

# ---------------------------------------------------------------------------
# 6. The ruling's stop condition, as one table in the log
# ---------------------------------------------------------------------------
$real = $evidence.runs['none']
Write-Host ''
Write-Host 'W4-A4 :: the A -> B read-back'
Write-Host ('  {0,-34} {1}' -f 'A version / product', "$($aFacts.productVersion)  $($aFacts.productCode)")
Write-Host ('  {0,-34} {1}' -f 'B version / product',
    "$($evidence.packages.b['none'].productVersion)  $($evidence.packages.b['none'].productCode)")
Write-Host ('  {0,-34} {1}' -f 'UpgradeCode in A', $aFacts.upgradeCode)
Write-Host ('  {0,-34} {1}' -f 'UpgradeCode in B', $evidence.packages.b['none'].upgradeCode)
Write-Host ('  {0,-34} {1}' -f 'exe after A', $real.installedExeAfterA)
Write-Host ('  {0,-34} {1}' -f 'exe after B', $real.installedExeAfterB)
Write-Host ('  {0,-34} {1}' -f 'accepted exe (B was built from)', $pristineExeSha)
Write-Host ('  {0,-34} {1}' -f 'msiexec /i B over A', $real.upgradeExit)
Write-Host ('  {0,-34} {1}' -f 'A still installed after B', $real.aProductInstalled)
Write-Host ('  {0,-34} {1}' -f 'B installed after B', $real.bProductInstalled)
Write-Host ('  {0,-34} {1}' -f 'service state after B', $real.serviceState)
Write-Host ('  {0,-34} {1}' -f 'service binPath after B', $real.serviceImagePath)
foreach ($name in 'config', 'data', 'cache', 'log') {
    $entry = $real.stateAfterUpgrade[$name]
    Write-Host ('  {0,-34} directory {1,-5} sentinel {2,-5} {3}' -f `
        "state '$name' after B", $entry.directory, $entry.sentinel,
        $(if ($entry.sha256) { $entry.sha256 } else { '(none)' }))
}
Write-Host ('  {0,-34} {1}' -f 'sentinel written between installs', $real.sentinelSha256)
foreach ($label in $real.acls.Keys) {
    $acl = $real.acls[$label]
    if ($null -eq $acl) { Write-Host ('  {0,-34} (absent)' -f "acl '$label' after B"); continue }
    Write-Host ('  {0,-34} protected {1,-5} {2}' -f "acl '$label' after B", $acl.protected, $acl.sddl)
}
Write-Host ''

$evidence.installPrefixKind = $(if ($real.programFilesTesserafinExists) {
    'real %ProgramFiles% -- the INSTALLFOLDER override did not take'
} else {
    'disposable prefix, %ProgramFiles% untouched'
})
$evidence.allPassed = $allPassed
Save-Evidence

if (-not $allPassed) {
    Write-Host 'W4-A4 REFUSED: at least one pair did not grade as declared'
    exit 1
}
Write-Note "all $($CONTROLS.Count) pairs graded as declared; prefix: $($evidence.installPrefixKind)"
exit 0
