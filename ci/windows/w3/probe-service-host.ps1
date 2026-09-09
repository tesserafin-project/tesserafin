#Requires -Version 7.2
<#
.SYNOPSIS
    W3-A0 and W3-A1 (#234): measure the Windows Service Control Manager
    boundary, the fatal-startup exit contract, and the absence of a linger after
    a pre-configuration failure, on a native Windows host.

.DESCRIPTION
    This script proves the things the W3-A0 and W3-A1 rulings authorise, and
    nothing else. It installs nothing outside the Service Control Manager,
    writes no machine-wide setting, publishes nothing, and deletes every service
    it makes.

    ── The four controls ────────────────────────────────────────────────────

    P  POSITIVE. The package's own `tesserafin-server-service.ps1` -- the
       accepted W2-A5 script, run unmodified from inside the extracted archive
       -- registers the service with W0 §4's exact argument list, including
       `--service`, `--webdir` and `--ffmpeg`. `sc start` must succeed, error
       1053 must NOT appear, the service must reach RUNNING, the server must
       answer on a port it actually bound, and `sc stop` must leave the service
       STOPPED with exit code 0 and no surviving process.

    N  NEGATIVE. The same executable and the same `--service`, with `--ffmpeg`
       withheld and no encoder reachable on PATH. W0 §2.5 measured the
       unmodified server logging `FfmpegException: Failed to find valid ffmpeg`
       and then exiting 0, which the SCM reads as a service that stopped
       normally. The service must end STOPPED with a NON-ZERO exit code and no
       surviving process.

       `sc start` is expected to SUCCEED here, and asserting otherwise would be
       wrong. `Host.StartAsync` answers the SCM through
       `IHostLifetime.WaitForStartAsync` before it starts any hosted service, so
       by the time the encoder check runs -- inside the startup tasks, after
       Kestrel has bound -- the service has already reported RUNNING. A service
       host that reported a failed START here would be one that had not answered
       the SCM yet, which is the 1053 this slice exists to close. The failure is
       therefore visible where it actually lives: in the STOP.

    C  BOUNDARY CONTROL. Byte-for-byte the negative control's command line with
       `--service` removed. W0 §4 measured error 1053 on the unmodified console
       executable; this control must still reproduce it on this head. Without
       it, P proves only that the server starts, not that `--service` is what
       makes it a service: a build in which the boundary had been wired
       unconditionally, or not at all, would be indistinguishable.

    F  W3-A1. The same `--service` command line as P, `--ffmpeg` INCLUDED, and
       an `encoding.xml` whose `TranscodingTempPath` cannot be created because a
       regular file already occupies its parent's name. That makes
       `EncodingConfigurationExtensions.GetTranscodePath` throw at the first
       statement after the host is built, which is before
       `configurationCompleted` -- the path W3-A0 §3 recorded as still lingering
       for ten minutes and orphaning under the SCM. The service must end STOPPED
       with a non-zero exit code, no process may survive, and the time from the
       failure appearing in the log to the service reaching STOPPED must be a
       small fraction of ten minutes.

       That last measurement is deliberately taken from the FAILURE and not from
       `sc start`. F's fault fires after the startup migrations, so a bound
       measured from the start would be a bound on how fast the runner creates a
       database; measured from the failure, it is the linger and only the
       linger.

       The encoder is present here, and `FfmpegException` must NOT appear. N is
       the encoder control; F is a different hook, and asserting the encoder's
       absence from F's log is what keeps the two from quietly becoming one.

    ── What this script deliberately does not do ────────────────────────────

      * it registers no `NT SERVICE\Tesserafin` identity and grants no ACL --
        W0 §9 and W4;
      * it writes no `ServicesPipeTimeout` and no other machine-wide value;
      * it builds no MSI and writes no Add/Remove Programs entry;
      * it edits none of the W2 scripts it runs, and copies none of them;
      * it uploads nothing and publishes nothing.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string] $RepoRoot,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string] $WorkDir,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string] $EvidencePath,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string] $SourceDateEpoch,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string] $OrasPath,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string] $HeadSha,
    [string] $PythonPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# `sc.exe binPath=` carries one string containing quoted, space-bearing paths.
# Standard argument passing is the mode in which that string arrives at sc.exe
# intact; W2-A5 NB-2 records it as the correct construction and as unmeasured,
# and this script is where it stops being unmeasured.
$PSNativeCommandArgumentPassing = 'Standard'

$SERVICE_POSITIVE = 'Tesserafin'
$SERVICE_NEGATIVE = 'TesserafinW3NoEncoder'
$SERVICE_BOUNDARY = 'TesserafinW3NoFlag'
$SERVICE_PRECONFIG = 'TesserafinW3PreConfig'
$ALL_SERVICES = @($SERVICE_POSITIVE, $SERVICE_NEGATIVE, $SERVICE_BOUNDARY, $SERVICE_PRECONFIG)

# A cold first start creates the database and applies every migration. W0 §2.3
# allowed 600 s for exactly that on this runner image and recorded a run still
# migrating at 180 s, so a tighter budget here would measure the runner.
$READY_TIMEOUT_SECONDS = 900
$STOP_TIMEOUT_SECONDS = 300

# W3-A1. `Program.PreConfigurationFailureLinger` is ten minutes; master waits all
# of it before the process ends. This is the bound on what F is allowed to be,
# measured from the failure rather than from the start, and it is five times the
# whole of what the repaired path actually needs.
$LINGER_BUDGET_SECONDS = 120

$evidence = [ordered]@{
    schemaVersion = 2
    document = 'tesserafin-w3-a0-service-host'
    slice = 'W3-A0 + W3-A1'
    tracker = 'https://github.com/tesserafin-project/tesserafin/issues/234'
    contract = 'docs/distribution/W0-windows-server.md §2.5 and §4; W3-A0 §3'
    headSha = $HeadSha
    runner = [ordered]@{
        os = [System.Environment]::OSVersion.VersionString
        powerShell = $PSVersionTable.PSVersion.ToString()
    }
    encoderOnPath = $null
    package = [ordered]@{}
    controls = [ordered]@{}
    # Named so a reader does not have to infer them from an absence.
    registeredIdentity = $false
    wroteMachineWideSettings = $false
    builtInstaller = $false
    published = $false
}

function Save-Evidence {
    $directory = [System.IO.Path]::GetDirectoryName($EvidencePath)
    if ($directory) { $null = [System.IO.Directory]::CreateDirectory($directory) }
    $json = ($evidence | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($EvidencePath, $json + "`n")
}

# Evidence is worth more than a clean stack: a probe that dies half way through
# still measured something, and losing it forces a whole hosted run to be
# repeated to learn what was already known. `break` rethrows, so the job still
# fails -- it just fails with a ledger attached.
trap {
    Remove-AllServices
    Save-Evidence
    break
}

function Deny {
    param([string] $Category, [string] $Message, [string] $Slice = 'W3-A0')
    throw "$Slice REFUSED [$Category]: $Message"
}

function Write-Note {
    param([string] $Message, [string] $Slice = 'W3-A0')
    [Console]::Out.WriteLine("${Slice}: $Message")
}

function Invoke-Sc {
    param([Parameter(Mandatory = $true)] [string[]] $Arguments)
    $output = & sc.exe @Arguments 2>&1 | Out-String
    return [ordered]@{ exitCode = $LASTEXITCODE; output = $output.Trim() }
}

function Get-ServiceFacts {
    param([Parameter(Mandatory = $true)] [string] $Name)
    # CIM, not `sc.exe query` text. W2-A5 NB-3 records that the STATE label
    # sc.exe prints is localisable; Win32_Service.State is not, and it is also
    # the only place ExitCode and ProcessId can be read as numbers rather than
    # parsed back out of a table.
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    if ($null -eq $service) { return $null }
    return [ordered]@{
        name = $service.Name
        state = $service.State
        processId = [int] $service.ProcessId
        exitCode = [int] $service.ExitCode
        serviceSpecificExitCode = [int] $service.ServiceSpecificExitCode
        startMode = $service.StartMode
        startName = $service.StartName
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
    # EVERY caller wraps this in @(). `return @()` unrolls on the way out and the
    # caller receives $null, so under Set-StrictMode the `.Count` that reads "no
    # orphans survived" throws instead of reporting zero -- which is what turned
    # a passing control into a crash on the first run that got this far. The
    # `return ,@(...)` idiom fixes the assignment shape but NOT `foreach`, which
    # then iterates once over the empty array, so it is not used here: one rule
    # that holds for both consumption shapes beats two that each hold for one.
    return @(Get-Process -Name 'tesserafin' -ErrorAction SilentlyContinue |
        ForEach-Object { [ordered]@{ id = $_.Id; path = $_.Path } })
}

function Remove-AllServices {
    foreach ($name in $ALL_SERVICES) {
        & sc.exe stop $name *>&1 | Out-Null
        Start-Sleep -Milliseconds 500
        & sc.exe delete $name *>&1 | Out-Null
    }
    foreach ($orphan in @(Get-ServerProcesses)) {
        Stop-Process -Id $orphan.id -Force -ErrorAction SilentlyContinue
    }
}

function New-StateDirectories {
    param([Parameter(Mandatory = $true)] [string] $Root)
    $state = [ordered]@{}
    foreach ($name in 'config', 'data', 'cache', 'log') {
        $path = [System.IO.Path]::Combine($Root, $name)
        $null = [System.IO.Directory]::CreateDirectory($path)
        $state[$name] = $path
    }
    return $state
}

function Get-FfmpegExceptionSeen {
    param([Parameter(Mandatory = $true)] [string] $LogDir)
    if (-not [System.IO.Directory]::Exists($LogDir)) { return $false }
    foreach ($file in Get-ChildItem -LiteralPath $LogDir -Filter '*.log' -File -ErrorAction SilentlyContinue) {
        if ((Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue) -match 'FfmpegException') {
            return $true
        }
    }
    return $false
}

function Test-LogContains {
    param(
        [Parameter(Mandatory = $true)] [string] $LogDir,
        [Parameter(Mandatory = $true)] [string] $Pattern
    )
    if (-not [System.IO.Directory]::Exists($LogDir)) { return $false }
    foreach ($file in Get-ChildItem -LiteralPath $LogDir -Filter '*.log' -File -ErrorAction SilentlyContinue) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
        if ($null -ne $text -and $text.Contains($Pattern, [System.StringComparison]::Ordinal)) {
            return $true
        }
    }
    return $false
}

function Test-ServerReady {
    param([Parameter(Mandatory = $true)] [int] $ProcessId)
    # W0 §2.3's readiness, all four traps closed: the port comes from the
    # process's own listening sockets rather than from configuration, a redirect
    # is an answer, a 503 from the startup SetupServer is not readiness, and the
    # process must still be alive three seconds after it answered.
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
                    # W0 §2.3 trap 4: a dying server can serve one response on
                    # its way out, and a probe that samples once records it as
                    # started.
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

# ── 0. preconditions ────────────────────────────────────────────────────────

if (-not $IsWindows) { Deny 'platform' 'the Service Control Manager exists only on Windows' }
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([System.Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Deny 'privilege' 'registering a service requires administrator'
}

# The negative control's premise, measured rather than assumed. W0 §2.5 found
# `windows-latest` ships no ffmpeg at all; if that ever changes, the negative
# control would silently find an encoder on PATH and stop being a control.
$encoder = Get-Command 'ffmpeg.exe' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
$evidence.encoderOnPath = if ($encoder) { $encoder.Source } else { $null }
if ($encoder) {
    Deny 'precondition' ("this runner has an encoder on PATH at '$($encoder.Source)'. The negative " +
        'control withholds --ffmpeg to prove the fatal path, and a PATH encoder would let the ' +
        'server start instead.')
}

Remove-AllServices

$repo = [System.IO.Path]::GetFullPath($RepoRoot)
$work = [System.IO.Path]::GetFullPath($WorkDir)
if ([System.IO.Directory]::Exists($work) -and
    @([System.IO.Directory]::EnumerateFileSystemEntries($work)).Count -gt 0) {
    Deny 'work-dir' "'$work' is not empty"
}
$null = [System.IO.Directory]::CreateDirectory($work)

# ── 1. the package, from the frozen W2 assembler, in this job ───────────────
#
# The executable under test has to be built from THIS head: the accepted W2
# archive carries the stock exe, which reproduces 1053 by construction and would
# make a green run meaningless. What is consumed by accepted digest is what W2
# pins -- the Web payload and the FFmpeg runtime -- through the frozen assembler
# itself, unedited and uncopied.
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
$packageRoot = $tops[0].FullName

$serviceScript = [System.IO.Path]::Combine($packageRoot, 'tesserafin-server-service.ps1')
if (-not [System.IO.File]::Exists($serviceScript)) {
    Deny 'package' 'the package carries no tesserafin-server-service.ps1'
}

function Get-PackageRelativePath {
    param([Parameter(Mandatory = $true)] [string] $Constant)
    # Read from the accepted W2-A5 script rather than restated here. The
    # positive control registers through that script, so a second statement of
    # the layout in this file could disagree with the one actually used and the
    # disagreement would surface as a refusal about a path nothing registers.
    $match = [regex]::Match(
        [System.IO.File]::ReadAllText($serviceScript),
        "(?m)^\`$$Constant\s*=\s*'([^']+)'\s*$")
    if (-not $match.Success) {
        Deny 'package' ("the packaged service script does not define `$$Constant, so the package " +
            'layout cannot be read from the script that registers it')
    }
    return ($match.Groups[1].Value -replace '/', [System.IO.Path]::DirectorySeparatorChar)
}

$serverExe = [System.IO.Path]::Combine($packageRoot, (Get-PackageRelativePath 'SERVER_RELATIVE_EXE'))
$webDir = [System.IO.Path]::Combine($packageRoot, (Get-PackageRelativePath 'WEB_RELATIVE_DIR'))
$ffmpegExe = [System.IO.Path]::Combine($packageRoot, (Get-PackageRelativePath 'FFMPEG_RELATIVE_EXE'))
foreach ($required in $serverExe, $ffmpegExe) {
    if (-not [System.IO.File]::Exists($required)) { Deny 'package' "the package has no '$required'" }
}
if (-not [System.IO.Directory]::Exists($webDir)) { Deny 'package' "the package has no '$webDir'" }

$evidence.package = [ordered]@{
    archiveName = $archive.Name
    archiveSha256 = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    topLevelDirectory = $tops[0].Name
    # The executable is built here, from this commit, and is NOT the accepted
    # W2 binary. Recorded so a reviewer can see which exe answered the SCM.
    serverExeSha256 = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash.ToLowerInvariant()
    serverExeBuiltInJobFromHead = $true
}
Write-Note "package $($archive.Name), server exe $($evidence.package.serverExeSha256)"

# ── 2. P: the accepted W2 service script, unmodified ────────────────────────

$positiveState = New-StateDirectories ([System.IO.Path]::Combine($work, 'state-positive'))
& $serviceScript register `
    -DataDir $positiveState.data -ConfigDir $positiveState.config `
    -CacheDir $positiveState.cache -LogDir $positiveState.log
if ($LASTEXITCODE -ne 0) { Deny 'positive' 'the accepted W2 service script refused to register' }

$startWatch = [System.Diagnostics.Stopwatch]::StartNew()
$positiveStart = Invoke-Sc -Arguments @('start', $SERVICE_POSITIVE)
$startWatch.Stop()

$running = Wait-ServiceState -Name $SERVICE_POSITIVE -Expected 'Running' -TimeoutSeconds 120
$readiness = $null
if ($null -ne $running -and $running.state -eq 'Running' -and $running.processId -ne 0) {
    $deadline = [DateTime]::UtcNow.AddSeconds($READY_TIMEOUT_SECONDS)
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $readiness) {
        $readiness = Test-ServerReady -ProcessId $running.processId
        if ($null -eq $readiness) { Start-Sleep -Seconds 5 }
    }
}

$positiveStop = Invoke-Sc -Arguments @('stop', $SERVICE_POSITIVE)
$stopped = Wait-ServiceState -Name $SERVICE_POSITIVE -Expected 'Stopped' -TimeoutSeconds $STOP_TIMEOUT_SECONDS
Start-Sleep -Seconds 2
$positiveOrphans = @(Get-ServerProcesses)

$evidence.controls['P.positive'] = [ordered]@{
    intent = 'the accepted W2 script registers with --service, --webdir and --ffmpeg; SCM start succeeds, no 1053, the server answers, and a clean stop reports exit code 0'
    start = $positiveStart
    startSeconds = [math]::Round($startWatch.Elapsed.TotalSeconds, 1)
    error1053 = ($positiveStart.output -match '\b1053\b')
    running = $running
    readiness = $readiness
    stop = $positiveStop
    stopped = $stopped
    orphans = $positiveOrphans
}

& $serviceScript remove | Out-Null

if ($evidence.controls['P.positive'].error1053) {
    Deny 'positive' 'error 1053 still reproduces with --service; the service-host boundary is not closed'
}
if ($positiveStart.exitCode -ne 0) {
    Deny 'positive' "sc start failed with exit code $($positiveStart.exitCode): $($positiveStart.output)"
}
if ($null -eq $running -or $running.state -ne 'Running') {
    Deny 'positive' "the service never reached RUNNING (state '$(if ($running) { $running.state } else { 'absent' })')"
}
if ($null -eq $readiness -or -not $readiness.ready) {
    Deny 'positive' 'the running service never answered on a port it had bound'
}
if ($null -eq $stopped -or $stopped.state -ne 'Stopped') {
    Deny 'positive' 'the service did not reach STOPPED after sc stop'
}
if ($stopped.exitCode -ne 0) {
    Deny 'positive' ("a clean stop reported exit code $($stopped.exitCode). W3-A0 must not turn an " +
        'ordinary stop into a failure.')
}
if ($positiveOrphans.Count -ne 0) {
    Deny 'positive' "$($positiveOrphans.Count) tesserafin process(es) survived the stop"
}
Write-Note "P: RUNNING in $($evidence.controls['P.positive'].startSeconds)s, ready on port $($readiness.port) ($($readiness.status)), stopped with exit code 0"

# ── 3. N: --service, no encoder ─────────────────────────────────────────────

$negativeState = New-StateDirectories ([System.IO.Path]::Combine($work, 'state-negative'))
$negativeArguments = @(
    '--service'
    '--configdir', $negativeState.config
    '--datadir', $negativeState.data
    '--cachedir', $negativeState.cache
    '--logdir', $negativeState.log
    '--webdir', $webDir
)

function Format-BinPath {
    param([string] $Executable, [string[]] $Arguments)
    $parts = @('"' + $Executable + '"')
    foreach ($argument in $Arguments) {
        if ($argument.StartsWith('--')) { $parts += $argument } else { $parts += '"' + $argument + '"' }
    }
    return ($parts -join ' ')
}

$negativeBinPath = Format-BinPath -Executable $serverExe -Arguments $negativeArguments
$negativeCreate = Invoke-Sc -Arguments @(
    'create', $SERVICE_NEGATIVE, 'binPath=', $negativeBinPath, 'start=', 'demand',
    'DisplayName=', 'Tesserafin W3-A0 no-encoder control')
if ($negativeCreate.exitCode -ne 0) { Deny 'negative' "sc create failed: $($negativeCreate.output)" }

$negativeStart = Invoke-Sc -Arguments @('start', $SERVICE_NEGATIVE)
$negativeStopped = Wait-ServiceState -Name $SERVICE_NEGATIVE -Expected 'Stopped' -TimeoutSeconds $READY_TIMEOUT_SECONDS
Start-Sleep -Seconds 2
$negativeOrphans = @(Get-ServerProcesses)
$negativeFfmpeg = Get-FfmpegExceptionSeen -LogDir $negativeState.log

$evidence.controls['N.negative'] = [ordered]@{
    intent = 'the same executable and --service with --ffmpeg withheld: the SCM must see a non-zero exit code, not a normal stop'
    binPath = $negativeBinPath
    start = $negativeStart
    # Recorded, never asserted. See the header: the SCM handshake completes
    # before the encoder check runs, so a successful start here is the correct
    # shape and a failed one would mean the boundary had not been reached.
    startSucceeded = ($negativeStart.exitCode -eq 0)
    stopped = $negativeStopped
    ffmpegExceptionInLog = $negativeFfmpeg
    orphans = $negativeOrphans
}

$null = Invoke-Sc -Arguments @('delete', $SERVICE_NEGATIVE)

if ($null -eq $negativeStopped -or $negativeStopped.state -ne 'Stopped') {
    Deny 'negative' 'the encoderless service never reached STOPPED'
}
if (-not $negativeFfmpeg) {
    Deny 'negative' ('no FfmpegException in the service log, so whatever killed this start was not ' +
        'the missing encoder and the control proves nothing')
}
if ($negativeStopped.exitCode -eq 0 -and $negativeStopped.serviceSpecificExitCode -eq 0) {
    Deny 'negative' ('the SCM recorded exit code 0 for a fatal startup. That is exactly the W0 §2.5 ' +
        'defect: a service that stopped normally, with no failure action and no record that the ' +
        'server never came up.')
}
if ($negativeOrphans.Count -ne 0) {
    Deny 'negative' "$($negativeOrphans.Count) tesserafin process(es) survived the failed start"
}
Write-Note "N: stopped, Win32 exit code $($negativeStopped.exitCode), service-specific $($negativeStopped.serviceSpecificExitCode), FfmpegException in log"

# ── 4. C: the same command line without --service ───────────────────────────

$boundaryState = New-StateDirectories ([System.IO.Path]::Combine($work, 'state-boundary'))
$boundaryArguments = @(
    '--configdir', $boundaryState.config
    '--datadir', $boundaryState.data
    '--cachedir', $boundaryState.cache
    '--logdir', $boundaryState.log
    '--webdir', $webDir
)
$boundaryBinPath = Format-BinPath -Executable $serverExe -Arguments $boundaryArguments
$boundaryCreate = Invoke-Sc -Arguments @(
    'create', $SERVICE_BOUNDARY, 'binPath=', $boundaryBinPath, 'start=', 'demand',
    'DisplayName=', 'Tesserafin W3-A0 no-flag control')
if ($boundaryCreate.exitCode -ne 0) { Deny 'boundary' "sc create failed: $($boundaryCreate.output)" }

$boundaryWatch = [System.Diagnostics.Stopwatch]::StartNew()
$boundaryStart = Invoke-Sc -Arguments @('start', $SERVICE_BOUNDARY)
$boundaryWatch.Stop()
Start-Sleep -Seconds 3
$boundaryFacts = Get-ServiceFacts -Name $SERVICE_BOUNDARY
$boundaryOrphans = @(Get-ServerProcesses)

$evidence.controls['C.boundary'] = [ordered]@{
    intent = "the negative control's command line with --service removed: W0 §4's error 1053 must still reproduce, so P is attributable to the flag"
    binPath = $boundaryBinPath
    start = $boundaryStart
    startSeconds = [math]::Round($boundaryWatch.Elapsed.TotalSeconds, 1)
    error1053 = ($boundaryStart.output -match '\b1053\b')
    facts = $boundaryFacts
    orphans = $boundaryOrphans
}

foreach ($orphan in $boundaryOrphans) { Stop-Process -Id $orphan.id -Force -ErrorAction SilentlyContinue }
$null = Invoke-Sc -Arguments @('stop', $SERVICE_BOUNDARY)
$null = Invoke-Sc -Arguments @('delete', $SERVICE_BOUNDARY)

if (-not $evidence.controls['C.boundary'].error1053) {
    Deny 'boundary' ("the SCM did not answer 1053 for the same command line without --service " +
        "(exit $($boundaryStart.exitCode): $($boundaryStart.output)). Either the boundary is wired " +
        'regardless of the flag, or this control no longer measures it.')
}
Write-Note "C: 1053 reproduced in $($evidence.controls['C.boundary'].startSeconds)s without --service"

# ── 5. F: a fault before configurationCompleted, under --service ────────────
#
# W3-A0 §3's named residual. The hook is real and is named here rather than
# invented: `Program.StartServer` calls
# `EncodingConfigurationExtensions.GetTranscodePath` as its first statement
# after `Host...Build()`, and that helper creates the configured
# `TranscodingTempPath` if it is missing. A path whose parent is a regular file
# cannot be created, so the call throws -- before `configurationCompleted`, with
# no encoder involved and no database work between the two.

$preConfigState = New-StateDirectories ([System.IO.Path]::Combine($work, 'state-preconfig'))

# A file, not a directory, standing exactly where a directory has to be made.
# This is a real operator misconfiguration reached through the real encoding.xml
# -- the shape a moved or half-restored library takes -- and not a flag added to
# the server to make a control fail.
$occupied = [System.IO.Path]::Combine($work, 'state-preconfig', 'not-a-directory')
[System.IO.File]::WriteAllText($occupied, '')
$unreachableTranscodePath = [System.IO.Path]::Combine($occupied, 'transcodes')
[System.IO.File]::WriteAllText(
    [System.IO.Path]::Combine($preConfigState.config, 'encoding.xml'),
    @"
<?xml version="1.0" encoding="utf-8"?>
<EncodingOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <TranscodingTempPath>$unreachableTranscodePath</TranscodingTempPath>
</EncodingOptions>
"@)

# P's argument list, `--ffmpeg` included. F is not a second encoder control.
$preConfigArguments = @(
    '--service'
    '--configdir', $preConfigState.config
    '--datadir', $preConfigState.data
    '--cachedir', $preConfigState.cache
    '--logdir', $preConfigState.log
    '--webdir', $webDir
    '--ffmpeg', $ffmpegExe
)
$preConfigBinPath = Format-BinPath -Executable $serverExe -Arguments $preConfigArguments
$preConfigCreate = Invoke-Sc -Arguments @(
    'create', $SERVICE_PRECONFIG, 'binPath=', $preConfigBinPath, 'start=', 'demand',
    'DisplayName=', 'Tesserafin W3-A1 pre-configuration failure control')
if ($preConfigCreate.exitCode -ne 0) {
    Deny 'preconfig' "sc create failed: $($preConfigCreate.output)" -Slice 'W3-A1'
}

$preConfigWatch = [System.Diagnostics.Stopwatch]::StartNew()
$preConfigStart = Invoke-Sc -Arguments @('start', $SERVICE_PRECONFIG)

# One loop, two stamps. The failure stamp is what makes the linger measurable
# independently of how long this runner takes to reach the fault: F's hook fires
# after the startup migrations, so a budget counted from `sc start` would be a
# budget on database creation.
$faultSeenAtSeconds = $null
$preConfigStopped = $null
$deadline = [DateTime]::UtcNow.AddSeconds($READY_TIMEOUT_SECONDS)
while ([DateTime]::UtcNow -lt $deadline) {
    if ($null -eq $faultSeenAtSeconds -and
        (Test-LogContains -LogDir $preConfigState.log -Pattern 'Error while starting server')) {
        $faultSeenAtSeconds = [math]::Round($preConfigWatch.Elapsed.TotalSeconds, 1)
    }

    $facts = Get-ServiceFacts -Name $SERVICE_PRECONFIG
    if ($null -eq $facts) { break }
    if ($facts.state -eq 'Stopped') { $preConfigStopped = $facts; break }
    Start-Sleep -Seconds 1
}
$preConfigWatch.Stop()
$stoppedAtSeconds = [math]::Round($preConfigWatch.Elapsed.TotalSeconds, 1)
if ($null -eq $preConfigStopped) { $preConfigStopped = Get-ServiceFacts -Name $SERVICE_PRECONFIG }
Start-Sleep -Seconds 2
$preConfigOrphans = @(Get-ServerProcesses)
$preConfigFfmpeg = Get-FfmpegExceptionSeen -LogDir $preConfigState.log
# The FILE, not the full transcode path. .NET names the unreachable path on
# Unix ("Could not find a part of the path '<transcodes>'") and the colliding
# entry on Windows ("Cannot create '<not-a-directory>' because a file or
# directory with the same name already exists"). The file's path is a prefix of
# the transcode path, so it is the one substring both messages carry.
$preConfigHookSeen = Test-LogContains -LogDir $preConfigState.log -Pattern $occupied

$evidence.controls['F.preconfig'] = [ordered]@{
    intent = 'a fault before configurationCompleted, under --service and with an encoder present: the service must end STOPPED with a non-zero exit code and no orphan, and it must not wait out the ten-minute linger'
    hook = 'Tesserafin.Common.Configuration.EncodingConfigurationExtensions.GetTranscodePath, called from Program.StartServer immediately after the host is built'
    unreachableTranscodePath = $unreachableTranscodePath
    occupiedByFile = $occupied
    binPath = $preConfigBinPath
    start = $preConfigStart
    # Recorded, never asserted, for N's reason: the SCM handshake completes
    # before the hosted service starts the server at all.
    startSucceeded = ($preConfigStart.exitCode -eq 0)
    faultLoggedAfterSeconds = $faultSeenAtSeconds
    stoppedAfterSeconds = $stoppedAtSeconds
    lingerSeconds = $(if ($null -eq $faultSeenAtSeconds) { $null } else { [math]::Round($stoppedAtSeconds - $faultSeenAtSeconds, 1) })
    lingerBudgetSeconds = $LINGER_BUDGET_SECONDS
    masterLingerSeconds = 600
    stopped = $preConfigStopped
    hookInLog = $preConfigHookSeen
    ffmpegExceptionInLog = $preConfigFfmpeg
    orphans = $preConfigOrphans
}

$null = Invoke-Sc -Arguments @('delete', $SERVICE_PRECONFIG)

if (-not $preConfigHookSeen) {
    Deny 'preconfig' ("the blocked transcode path never appears in the service log, so whatever " +
        'happened here was not the pre-configurationCompleted hook and this control proves nothing') -Slice 'W3-A1'
}
if ($preConfigFfmpeg) {
    Deny 'preconfig' ('FfmpegException appears in the log of a control that was given --ffmpeg. F ' +
        'measures a different hook from N; if the encoder path fired, the fault under test did not.') -Slice 'W3-A1'
}
if ($null -eq $preConfigStopped -or $preConfigStopped.state -ne 'Stopped') {
    Deny 'preconfig' ("the service never reached STOPPED within $READY_TIMEOUT_SECONDS s (state " +
        "'$(if ($preConfigStopped) { $preConfigStopped.state } else { 'absent' })'). That is the " +
        'W3-A0 §3 residual: a service reported RUNNING with a dead server behind it.') -Slice 'W3-A1'
}
if ($null -eq $faultSeenAtSeconds) {
    Deny 'preconfig' 'the service stopped without ever logging a fatal startup' -Slice 'W3-A1'
}
$lingerSeconds = $evidence.controls['F.preconfig'].lingerSeconds
if ($lingerSeconds -gt $LINGER_BUDGET_SECONDS) {
    Deny 'preconfig' ("the service took $lingerSeconds s to stop after logging its failure, over the " +
        "$LINGER_BUDGET_SECONDS s budget. Master waits 600 s here, which under the SCM is a RUNNING " +
        'service with nothing behind it and then an orphaned tesserafin.exe.') -Slice 'W3-A1'
}
if ($preConfigStopped.exitCode -eq 0 -and $preConfigStopped.serviceSpecificExitCode -eq 0) {
    Deny 'preconfig' ('the SCM recorded exit code 0 for a startup that failed before ' +
        'configurationCompleted') -Slice 'W3-A1'
}
if ($preConfigOrphans.Count -ne 0) {
    Deny 'preconfig' "$($preConfigOrphans.Count) tesserafin process(es) survived the failed start" -Slice 'W3-A1'
}
Write-Note ("F: fault logged at $faultSeenAtSeconds s, stopped at $stoppedAtSeconds s " +
    "(linger $lingerSeconds s of a $LINGER_BUDGET_SECONDS s budget; master waits 600 s), " +
    "Win32 exit code $($preConfigStopped.exitCode), orphans $($preConfigOrphans.Count)") -Slice 'W3-A1'

Remove-AllServices
Save-Evidence
Write-Note "evidence written to $EvidencePath"

# `sc.exe delete` answers 1060 for a service that is already gone, which is the
# SUCCESSFUL outcome of the cleanup above and is also the last native command
# this script runs. Without an explicit exit the caller reads that 1060 out of
# $LASTEXITCODE and reports three passing controls as a refusal. Every refusal
# path throws, so reaching this line means the proof held.
exit 0
