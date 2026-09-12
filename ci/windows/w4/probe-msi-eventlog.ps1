#Requires -Version 7.2
<#
.SYNOPSIS
    W4-A6 (#234). Prove that the MSI registers the W0 §4 Windows Event Log
    source, that a start and a stop run AFTER the install write a
    service-lifecycle event under it, and that the uninstall removes it.

.DESCRIPTION
    The claim, in full and in the ruling's own words:

      after install, an Event Log source named `Tesserafin` exists. `sc start`
      then `sc stop` -- OUTSIDE the MSI transaction -- writes at least one
      service-lifecycle event under that source. Uninstall removes the source.

    WHAT WRITES THE EVENT. Nothing in this repository does.
    `System.ServiceProcess.ServiceBase` defaults `AutoLog` to true and writes one
    entry when `OnStart` returns and one when `OnStop` does, through an
    `EventLog` whose `Log` is `Application` and whose `Source` is the service
    name. `Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime`
    derives from `ServiceBase` and sets only `ServiceName` and `CanShutdown`, so
    the server W3-A0 put under the SCM has been trying to write these two events
    since W3-A0 and has had nowhere to put them.

    That is not a gap in the server. `EventLog.WriteEntry` CREATES a missing
    source before writing and creating one needs write access under
    `HKLM\SYSTEM\CurrentControlSet\Services\EventLog`, which
    `NT SERVICE\Tesserafin` -- the W0 §9.2 identity the package registers the
    service under -- does not have. `ServiceBase.WriteLogEntry` swallows the
    failure. So the events vanish silently, and the installer is the only thing
    on the machine that can close it. W3-A0 §3 recorded exactly that, as W4's.

    THE START IS A PROBE, NOT THE PACKAGE. W0 §10 leaves a fresh installation
    installed and enabled but NOT started, and this slice does not change that:
    the service is Stopped when msiexec exits, and this script starts it
    afterwards, the same shape as `ci/windows/w3/probe-service-host.ps1`. Two
    predicates say so -- one read off the live SCM and one read out of the
    package's own `ServiceControl` table -- because the two fail differently.

    THREE HOSTILE CONTROLS, all built from the REAL authoring through the REAL
    builder:

      eventlog-no-source        no source component and no reference to it.
                                Installs, starts and stops exactly like the real
                                package, and the lifecycle events go nowhere.
                                This control is the whole argument for the slice.

      eventlog-source-survives  one attribute, `Permanent="yes"`. Everything up
                                to the uninstall is what the real package does.

      eventlog-start-install    the package asks the installer to start the
                                service inside its own transaction. It is built
                                and its own tables are read, and it is
                                deliberately NOT installed -- see
                                `Get-W4EventLogControlExpectations`, which
                                carries the reason: since W3-A0 the executable
                                answers the SCM, so the package can now either
                                reach W0 §5.2's 1920-to-1603 rollback or install
                                cleanly with the service Running, and installing
                                it would grade the control on whichever of the
                                two the runner produced rather than on the defect.

    The ruling's fourth control, "UpgradeCode bytes moved", is not a run at all:
    it is `ci/windows/w4/msi-controls.py`'s frozen-GUID gate, which refuses the
    authoring before a package is built.

    WHAT THIS SCRIPT DOES NOT DO: it signs nothing, publishes nothing, exercises
    no repair and no upgrade, edits no `SharedVersion.cs`, changes no server
    behaviour, and claims no part of W4 accepted.

.PARAMETER RepoRoot
    The checkout under test.

.PARAMETER WorkDir
    An empty directory for the assembly, the packages and the msiexec logs.

.PARAMETER InstallPrefix
    The root under which each run gets its own INSTALLFOLDER, so no run installs
    into the runner's real %ProgramFiles%.

.PARAMETER EvidencePath
    Where the evidence document is written, and rewritten after every step so a
    cancelled job still leaves what it had measured.

.PARAMETER SourceDateEpoch
    Passed straight to the frozen W2-A2 assembler.

.PARAMETER OrasPath
    The pinned ORAS client, for the assembler's own Web payload pull.

.PARAMETER PythonPath
    The interpreter the assembler should use, when the runner has more than one.

.PARAMETER HeadSha
    Recorded in the evidence so the document says what it was measured on.
#>

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

# `sc.exe` takes ONE string containing quoted, space-bearing paths. Standard
# argument passing is the mode in which that string arrives intact, and W3's
# probe sets it for the same reason.
$PSNativeCommandArgumentPassing = 'Standard'

$SERVICE_NAME = 'Tesserafin'
$SERVICE_KEY = "HKLM:\SYSTEM\CurrentControlSet\Services\$SERVICE_NAME"
$RETAINED_STATE_KEY = 'HKLM:\SOFTWARE\Tesserafin'

# W0 §4's Event Log row. The source IS this key; there is no table and no custom
# action anywhere behind it.
$EVENT_LOG_NAME = 'Application'
$EVENT_LOG_SOURCE = 'Tesserafin'
# Written out rather than composed, because this exact string is the contract --
# `ci/windows/w4/msi-controls.py` asserts that the authoring and this file state
# the same one. The two halves it is made of are still named above, and the
# refusal below is what keeps the literal and the parts from drifting apart.
$EVENT_LOG_SOURCE_KEY = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\Tesserafin'
# The file the authoring's `EventMessageFile` names, relative to INSTALLFOLDER.
# It is a managed file of the `Microsoft.AspNetCore.App` win-x64 runtime pack, so
# it is in the self-contained publish the accepted W2 layout is -- and pointing
# at it is what keeps a distribution that needs no system .NET runtime from
# depending on the .NET Framework's `EventLogMessages.dll` for the sake of a
# string. Whether it is actually there is a graded predicate, not an assumption.
$MESSAGE_FILE_NAME = 'System.Diagnostics.EventLog.Messages.dll'

# A cold first start creates the database and applies every migration. W0 §2.3
# allowed 600 s for that on this runner image and recorded a run still migrating
# at 180 s; W3's probe uses 900 s and so does this one, for the same reason: a
# tighter budget would be measuring the runner.
$READY_TIMEOUT_SECONDS = 900
$STOP_TIMEOUT_SECONDS = 300

# `none` first: the run every other verdict is read against, and the live
# observation the table control is graded on. The survivor last, because it is
# the one control that deliberately leaves a source behind.
$LIVE_CONTROLS = @('none', 'eventlog-no-source', 'eventlog-source-survives')
$TABLE_CONTROLS = @('eventlog-start-install')

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, 'W4MsiAssertions.psm1')) -Force
Import-Module ([System.IO.Path]::Combine($PSScriptRoot, 'W4MsiInstruments.psm1')) -Force

$evidence = [ordered]@{
    slice = 'W4-A6'
    tracker = 234
    contract = 'docs/distribution/W0-windows-server.md §4 (Event Log) and §10'
    headSha = $HeadSha
    # Stated as data so the closing report cannot claim more than the run did.
    signed = $false
    published = $false
    # W0 §10 is about the PACKAGE. It is still true after W4-A6, and it is a
    # different question from whether this PROBE started the service, which it
    # must. Two names, because one flipped in meaning would be worse than none.
    packageStartedTheService = $false
    probeStartedTheService = $true
    exercisedRepair = $false
    exercisedMajorUpgrade = $false
    editedSharedVersion = $false
    registeredEventLogSource = $true
    definedAnEventLog = $false
    eventLogName = $EVENT_LOG_NAME
    eventLogSource = $EVENT_LOG_SOURCE
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
    Write-Host "W4-A6 REFUSED [$Reason]: $Detail"
    exit 1
}

function Write-Note { param([string] $Text) Write-Host "W4-A6 :: $Text" }

function Get-ServiceFacts {
    <#
        CIM, not `sc.exe query` text. W2-A5 NB-3 records that the STATE label
        `sc.exe` prints is localisable; `Win32_Service.State` is not, and it is
        also the only place the owning process id can be read as a number rather
        than parsed back out of a table.
    #>
    param([Parameter(Mandatory = $true)] [string] $Name)
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    if ($null -eq $service) { return $null }
    return [ordered]@{
        state = [string]$service.State
        processId = [int] $service.ProcessId
        exitCode = [int] $service.ExitCode
        startMode = [string]$service.StartMode
        startName = [string]$service.StartName
    }
}

function Wait-ServiceState {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $Expected,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $facts = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $facts = Get-ServiceFacts -Name $Name
        if ($null -eq $facts) { return $null }
        if ($facts.state -eq $Expected) { return $facts }
        Start-Sleep -Milliseconds 500
    }
    return $facts
}

function Get-ServerProcesses {
    # EVERY caller wraps this in @(), for the reason W3's probe spells out:
    # `return @()` unrolls on the way out and the caller receives $null, so
    # under Set-StrictMode the `.Count` that reads "no orphans survived" throws
    # instead of reporting zero.
    return @(Get-Process -Name 'tesserafin' -ErrorAction SilentlyContinue |
        ForEach-Object { [ordered]@{ id = $_.Id; path = $_.Path } })
}

function Test-ServerReady {
    <#
        W0 §2.3's readiness, with its four traps closed, copied in shape from
        `ci/windows/w3/probe-service-host.ps1`: the port comes from the process's
        own listening sockets rather than from configuration, a redirect is an
        answer, a 503 from the startup SetupServer is NOT readiness, and the
        process must still be alive three seconds after answering.

        It is here and not only in W3 because of what the stop does. The shell
        answers the SCM in milliseconds and the real server keeps starting behind
        it, so a `sc stop` issued the instant `Win32_Service.State` reads Running
        would arrive in the middle of the first migration. That would be
        measuring the shutdown path of a half-started server -- which is a real
        question, and is W3's, not this slice's.
    #>
    param([Parameter(Mandatory = $true)] [int] $ProcessId)
    $ports = @(Get-NetTCPConnection -OwningProcess $ProcessId -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty LocalPort -Unique)
    foreach ($port in $ports) {
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $client = [System.Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(10)
        try {
            $response = $client.GetAsync("http://127.0.0.1:$port/").GetAwaiter().GetResult()
            $status = [int] $response.StatusCode
            if ($status -ne 503) {
                Start-Sleep -Seconds 3
                if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
                    return [ordered]@{ ready = $false; port = $port; status = $status; aliveAfter3s = $false }
                }
                return [ordered]@{ ready = $true; port = $port; status = $status; aliveAfter3s = $true }
            }
        }
        catch {
            # Not this port.
        }
        finally {
            $client.Dispose()
            $handler.Dispose()
        }
    }
    return $null
}

function Reset-InstalledState {
    <#
        Put the machine back to "this package has never been installed", before
        the first run and between runs.

        Three removals, and each closes a way one run could be read as another's.
        `%ProgramData%\Tesserafin` holds the database, so without it the second
        and third starts would be warm ones and "the service started" would be a
        different measurement each time. `HKLM\SOFTWARE\Tesserafin` is the
        retained-state and remember-property key path: the retained components
        are NeverOverwrite, so with the key still present the installer SKIPS
        them. And the Event Log source key is what `eventlog-source-survives`
        deliberately leaves behind -- a run that inherited it would find a source
        registered that its own package never registered.
    #>
    Remove-Item -LiteralPath ([System.IO.Path]::Combine($env:ProgramData, 'Tesserafin')) `
        -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $RETAINED_STATE_KEY -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $EVENT_LOG_SOURCE_KEY -Recurse -Force -ErrorAction SilentlyContinue
    return (-not (Test-Path -LiteralPath $RETAINED_STATE_KEY)) -and
        (-not (Test-Path -LiteralPath $EVENT_LOG_SOURCE_KEY))
}

# ---------------------------------------------------------------------------
# Preconditions
# ---------------------------------------------------------------------------
if ($EVENT_LOG_SOURCE_KEY -ne
    "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\$EVENT_LOG_NAME\$EVENT_LOG_SOURCE") {
    Deny 'contract' ("the source key literal and the log and source names above disagree: " +
        "'$EVENT_LOG_SOURCE_KEY' is not the key for '$EVENT_LOG_SOURCE' under '$EVENT_LOG_NAME'")
}
if (-not $IsWindows) { Deny 'platform' 'this proof only means anything on a native Windows host' }
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Deny 'platform' "this job needs PowerShell 7 or newer; it has $($PSVersionTable.PSVersion)"
}
$identity = [System.Security.Principal.WindowsPrincipal]::new(
    [System.Security.Principal.WindowsIdentity]::GetCurrent())
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
    Deny 'precondition' ("'$programFilesTesserafin' already exists, so this run could not tell what it " +
        'installed apart from something that was already there')
}

if (-not (Reset-InstalledState)) {
    Deny 'precondition' ('the machine still carries this package''s registry state after a reset, so ' +
        'the first run would be measuring something it did not create')
}
# Asked through the API as well as through the registry, because it is the API
# every reader of the log uses and it answers on the SUBKEY alone.
if (Test-W4EventLogSourceExists -SourceName $EVENT_LOG_SOURCE) {
    Deny 'precondition' ("an Event Log source named '$EVENT_LOG_SOURCE' already exists on this host, " +
        'so "the install registered it" would be unattributable')
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

$extractRoot = [System.IO.Path]::Combine($work, 'pkg')
$null = [System.IO.Directory]::CreateDirectory($extractRoot)
[System.IO.Compression.ZipFile]::ExtractToDirectory($archives[0].FullName, $extractRoot)
$tops = @(Get-ChildItem -LiteralPath $extractRoot -Force)
if ($tops.Count -ne 1 -or -not $tops[0].PSIsContainer) {
    Deny 'top-level' "the archive extracted $($tops.Count) top-level entries"
}
$stageRoot = $tops[0].FullName
if (-not [System.IO.File]::Exists([System.IO.Path]::Combine($stageRoot, $serverRelativeExe))) {
    Deny 'stage' "the accepted stage has no '$serverRelativeExe'"
}

# The authoring's EventMessageFile names a file the accepted stage is supposed
# to carry. It is READ here as well as graded after the install, so that a stage
# without it is a refusal with a name rather than a hostile-control-shaped red.
$evidence.stagedMessageFile = [System.IO.File]::Exists(
    [System.IO.Path]::Combine($stageRoot, $MESSAGE_FILE_NAME))
if (-not $evidence.stagedMessageFile) {
    Deny 'stage' ("the accepted stage has no '$MESSAGE_FILE_NAME', which is the file the authoring's " +
        'EventMessageFile names. A package built from it would register a source whose message file ' +
        'does not exist')
}

# ---------------------------------------------------------------------------
# 2. The packages -- one real, three controls, all from the REAL authoring
# ---------------------------------------------------------------------------
$builder = [System.IO.Path]::Combine($PSScriptRoot, 'build-msi.ps1')
$harvestRoot = [System.IO.Path]::Combine($work, 'harvest')
$logDir = [System.IO.Path]::Combine($work, 'logs')
$null = [System.IO.Directory]::CreateDirectory($logDir)

function Build-Package {
    param([Parameter(Mandatory = $true)] [string] $Mutation)
    $name = $(if ($Mutation -eq 'none') { 'real' } else { $Mutation })
    $msiPath = [System.IO.Path]::Combine($work, "$name.msi")
    # The builder TALKS, and W4-A4-R1 recorded what happens when that chatter is
    # left on the output stream of a function whose caller wants a path. Merged,
    # re-emitted to the host and kept on disk; the output stream carries the path.
    $buildLogPath = [System.IO.Path]::Combine($logDir, "build-$name.log")
    $buildOutput = @(& $builder -RepoRoot $repo -StageRoot $stageRoot -HarvestRoot $harvestRoot `
            -OutPath $msiPath -Mutation $Mutation *>&1 | ForEach-Object { [string]$_ })
    $builderExit = $LASTEXITCODE
    Set-Content -LiteralPath $buildLogPath -Value ($buildOutput -join [System.Environment]::NewLine) `
        -Encoding utf8NoBOM
    foreach ($line in $buildOutput) { Write-Host $line }
    if ($builderExit -ne 0 -or -not [System.IO.File]::Exists($msiPath)) {
        Deny 'build' ("the MSI for mutation '$Mutation' was not built; the builder exited " +
            "$builderExit and its log is at '$buildLogPath'")
    }
    return $msiPath
}

$msi = [ordered]@{}
foreach ($control in ($LIVE_CONTROLS + $TABLE_CONTROLS)) {
    $msi[$control] = Build-Package -Mutation $control
    $evidence.packages[$control] = [ordered]@{
        sha256 = (Get-FileHash -LiteralPath $msi[$control] -Algorithm SHA256).Hash.ToLowerInvariant()
        startsServiceOnInstall = [bool](Get-W4MsiStartsServiceOnInstall -MsiPath $msi[$control] `
            -ServiceName $SERVICE_NAME)
    }
}
Save-Evidence

# The two facts that make the start-on-install control a control at all. Read
# out of the BUILT packages, before any of them is installed: a control that
# does not differ from the real package in the thing it is a control for could
# never have fired, and a real package that DID start the service would be the
# defect rather than the baseline.
if ($evidence.packages['none'].startsServiceOnInstall) {
    Deny 'authoring' ('the REAL package asks the installer to start the service inside the ' +
        'transaction. W0 §10 leaves a fresh installation installed and enabled but not started')
}
if (-not $evidence.packages['eventlog-start-install'].startsServiceOnInstall) {
    Deny 'control' ("the 'eventlog-start-install' package carries no start-on-install event for " +
        "'$SERVICE_NAME', so the control could never have fired")
}

# ---------------------------------------------------------------------------
# 3. The live runs
# ---------------------------------------------------------------------------
$allPassed = $true
$script:realObservation = $null

foreach ($control in $LIVE_CONTROLS) {
    Write-Note "=== $control"
    if (-not (Reset-InstalledState)) {
        Deny 'reset' "the machine could not be put back to a never-installed state before '$control'"
    }

    $prefix = [System.IO.Path]::Combine($prefixRoot, $control)
    $run = [ordered]@{ control = $control; tableDerived = $false; installPrefix = $prefix }

    $run.installExit = Invoke-W4Msi -Arguments @('/i', "`"$($msi[$control])`"", "INSTALLFOLDER=`"$prefix`"") `
        -LogPath ([System.IO.Path]::Combine($logDir, "install-$control.log")) `
        -Label "install for '$control'"
    if ($run.installExit -ne 0) {
        # Every run in this slice is a run over an INSTALLED package. None of
        # the three controls is about an install that fails, so a non-zero here
        # is a refusal rather than a red: grading it would be grading the
        # rollback and not the defect.
        Deny 'install' ("msiexec /i exited $($run.installExit) for '$control'. Every run in this " +
            'slice is a run over an installed package')
    }

    $sourceAfterInstall = Get-W4EventLogSource -LogName $EVENT_LOG_NAME -SourceName $EVENT_LOG_SOURCE
    $run.sourceAfterInstall = $sourceAfterInstall
    $run.messageFileInstalled = [System.IO.File]::Exists(
        [System.IO.Path]::Combine($prefix, $MESSAGE_FILE_NAME))
    $run.serviceStateAfterInstall = Get-W4ServiceState -ServiceName $SERVICE_NAME
    Write-Note ("after install: source $(if ($null -eq $sourceAfterInstall) { 'ABSENT' } else { 'present' })" +
        ", service $($run.serviceStateAfterInstall)")

    # ── the probe's own start, AFTER the transaction ────────────────────────
    # The window opens BEFORE the start and is passed to every read, so an event
    # written by an earlier run of this job on this host cannot be counted as
    # this run's.
    $since = (Get-Date).AddSeconds(-1)
    $run.eventWindowOpenedAt = $since.ToUniversalTime().ToString('o')

    $startResult = & sc.exe start $SERVICE_NAME 2>&1 | Out-String
    $run.startExit = $LASTEXITCODE
    $run.startOutput = $startResult.Trim()

    $running = Wait-ServiceState -Name $SERVICE_NAME -Expected 'Running' -TimeoutSeconds 120
    $run.serviceStateAfterStart = $(if ($null -eq $running) { 'Absent' } else { $running.state })

    # Wait for the SERVER, not only for the shell, before asking for a stop.
    $readiness = $null
    if ($null -ne $running -and $running.state -eq 'Running' -and $running.processId -gt 0) {
        $deadline = [DateTime]::UtcNow.AddSeconds($READY_TIMEOUT_SECONDS)
        while ([DateTime]::UtcNow -lt $deadline) {
            $readiness = Test-ServerReady -ProcessId $running.processId
            if ($null -ne $readiness -and $readiness.ready) { break }
            if ($null -eq (Get-Process -Id $running.processId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Seconds 5
        }
    }
    $run.readiness = $readiness

    $stopResult = & sc.exe stop $SERVICE_NAME 2>&1 | Out-String
    $run.stopExit = $LASTEXITCODE
    $run.stopOutput = $stopResult.Trim()
    $stopped = Wait-ServiceState -Name $SERVICE_NAME -Expected 'Stopped' -TimeoutSeconds $STOP_TIMEOUT_SECONDS
    $run.serviceStateAfterStop = $(if ($null -eq $stopped) { 'Absent' } else { $stopped.state })
    # The lifecycle entry for the stop is written as `OnStop` returns, which is
    # the same moment the SCM is told STOPPED. Two seconds so the Event Log
    # service has written it before it is read -- W3's probe waits the same.
    Start-Sleep -Seconds 2
    $run.orphansAfterStop = @(Get-ServerProcesses).Count

    $run.lifecycleEvents = @(Get-W4LifecycleEvents -LogName $EVENT_LOG_NAME `
        -SourceName $EVENT_LOG_SOURCE -Since $since)
    Write-Note ("start $($run.serviceStateAfterStart), stop $($run.serviceStateAfterStop), " +
        "$($run.lifecycleEvents.Count) lifecycle event(s) under '$EVENT_LOG_SOURCE'")
    foreach ($entry in $run.lifecycleEvents) {
        Write-Note "  event $($entry.id) $($entry.level) $($entry.timeCreated) $($entry.message)"
    }

    # ── the uninstall ───────────────────────────────────────────────────────
    $run.uninstallExit = Invoke-W4Msi -Arguments @('/x', "`"$($msi[$control])`"") `
        -LogPath ([System.IO.Path]::Combine($logDir, "uninstall-$control.log")) `
        -Label "uninstall for '$control'"
    $run.sourceAfterUninstall = Get-W4EventLogSource -LogName $EVENT_LOG_NAME -SourceName $EVENT_LOG_SOURCE
    $run.sourceApiAfterUninstall = [bool](Test-W4EventLogSourceExists -SourceName $EVENT_LOG_SOURCE)
    Write-Note ("after uninstall: source " +
        "$(if ($null -eq $run.sourceAfterUninstall) { 'GONE' } else { 'STILL PRESENT' }), " +
        "SourceExists $($run.sourceApiAfterUninstall)")

    $observation = @{
        msi = @{ startsServiceOnInstall = $evidence.packages[$control].startsServiceOnInstall }
        installExit = $run.installExit
        uninstallExit = $run.uninstallExit
        installPrefix = $prefix
        messageFileName = $MESSAGE_FILE_NAME
        messageFileInstalled = $run.messageFileInstalled
        sourceAfterInstall = $sourceAfterInstall
        sourceAfterUninstall = $run.sourceAfterUninstall
        sourceApiAfterUninstall = $run.sourceApiAfterUninstall
        serviceStateAfterInstall = $run.serviceStateAfterInstall
        serviceStateAfterStart = $run.serviceStateAfterStart
        serviceStateAfterStop = $run.serviceStateAfterStop
        orphansAfterStop = $run.orphansAfterStop
        lifecycleEvents = $run.lifecycleEvents
    }
    $predicates = Get-W4EventLogPredicates -Observation $observation
    $verdict = Get-W4EventLogVerdict -Predicates $predicates -Control $control
    $run.predicates = $predicates
    $run.verdict = $verdict
    if ($control -eq 'none') { $script:realObservation = $observation }

    $evidence.runs[$control] = $run
    Save-Evidence
    Write-Note "$control -> $(if ($verdict.passed) { 'PASS' } else { 'FAIL' }): $($verdict.detail)"
    if (-not $verdict.passed) { $allPassed = $false }

    Remove-W4ServiceIfPresent -ServiceName $SERVICE_NAME -ServiceKey $SERVICE_KEY | Out-Null
    Remove-Item -LiteralPath $prefix -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 4. The table control
#
# Graded on the REAL run's live observation with only this package's own
# ServiceControl fact substituted -- which is exactly what the defect changes
# and nothing else, and is the shape `probe-msi-upgrade.ps1` already uses for
# its two table controls.
# ---------------------------------------------------------------------------
if ($null -eq $script:realObservation) {
    Deny 'order' 'the table control has no real run to be graded against'
}
foreach ($control in $TABLE_CONTROLS) {
    Write-Note "=== $control"
    $observation = $script:realObservation.Clone()
    $observation['msi'] = @{ startsServiceOnInstall = $evidence.packages[$control].startsServiceOnInstall }
    $predicates = Get-W4EventLogPredicates -Observation $observation
    $verdict = Get-W4EventLogVerdict -Predicates $predicates -Control $control
    $evidence.runs[$control] = [ordered]@{
        control = $control
        tableDerived = $true
        installedAnything = $false
        notInstalledBecause = ('since W3-A0 the executable answers the SCM, so this package can ' +
            'either reach W0 §5.2''s 1920-to-1603 rollback or install cleanly with the service ' +
            'Running. Both are the defect and neither is the other, so installing it would grade ' +
            'the control on whichever the runner produced rather than on the defect. What the ' +
            'defect IS is in the package''s own ServiceControl table either way')
        startsServiceOnInstall = $evidence.packages[$control].startsServiceOnInstall
        predicates = $predicates
        verdict = $verdict
    }
    Save-Evidence
    Write-Note "$control -> $(if ($verdict.passed) { 'PASS' } else { 'FAIL' }): $($verdict.detail)"
    if (-not $verdict.passed) { $allPassed = $false }
}

# ---------------------------------------------------------------------------
# 5. Leave nothing behind that a later run would inherit
# ---------------------------------------------------------------------------
$evidence.sourceLeftBehindByControl = [bool](Test-W4EventLogSourceExists -SourceName $EVENT_LOG_SOURCE)
Remove-Item -LiteralPath $EVENT_LOG_SOURCE_KEY -Recurse -Force -ErrorAction SilentlyContinue
$evidence.sourceRemovedAfterRun = -not (Test-Path -LiteralPath $EVENT_LOG_SOURCE_KEY)
Save-Evidence

if (-not $allPassed) {
    Write-Host 'W4-A6 FAILED: at least one run did not grade as declared'
    exit 1
}
Write-Host "W4-A6 PASSED: $($evidence.runs.Count) runs, every verdict as declared"
exit 0
