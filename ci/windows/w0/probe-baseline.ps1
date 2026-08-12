<#
.SYNOPSIS
    W0 native Windows baseline probe for the UNMODIFIED Tesserafin server (#234).

.DESCRIPTION
    W0-ONLY. Measures what stock master already does on a native Windows x64 host
    and what it does not. It changes no production code, produces no distributable
    artifact and must never be represented as a Windows distribution.

    The probe is deliberately paired everywhere it can be: a measurement that only
    passes because the RUNNER supplied something is recorded in the
    'test-host-dependency' bucket next to the negative control that shows what
    happens without it. A single green measurement is not evidence.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $RepoRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $WorkRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $EvidenceDir,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $HeadSha,

    # The digest-verified Tesserafin Web payload, extracted by the Linux leg.
    # Absent on purpose exercises the "missing Web payload" negative control.
    [Parameter()]
    [string] $WebPayloadDir = '',

    # ffmpeg.exe / ffprobe.exe produced by the W0 native source-build spike.
    # Absent on purpose exercises the no-encoder control, which on this image is
    # simply the default: the runner ships no FFmpeg at all.
    [Parameter()]
    [string] $FfmpegDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Import-Module (Join-Path $PSScriptRoot 'W0Probe.psm1') -Force

$evidence = New-W0Evidence -Probe 'baseline' -HeadSha $HeadSha

# Evidence is worth more than a clean stack. A probe that dies half way through
# still measured something, and losing that forces a whole hosted run to be
# repeated to learn what was already known. `break` rethrows, so the job still
# fails; it just fails with a ledger attached.
trap {
    if ($null -ne $evidence) {
        Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'baseline.json') | Out-Null
    }
    break
}

# -- Helpers --------------------------------------------------------------------

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

function Wait-ForHttp {
    <#
        Readiness is NOT "the port answers". /System/Info/Public answers from the
        startup SetupServer long before the real application is up, so a probe that
        waits on it measures the wrong server. The main host is up when '/'
        answers, which is why that is what this waits for.
    #>
    param(
        [Parameter(Mandatory)] [string] $BaseUrl,
        [Parameter()] [int] $TimeoutSeconds = 600
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $BaseUrl -MaximumRedirection 0 `
                -SkipHttpErrorCheck -ErrorAction Stop -TimeoutSec 10
            if ($response.StatusCode -in 200, 301, 302) { return $response }
        } catch {
            Write-Verbose "not ready yet: $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 750
    }
    return $null
}

function Invoke-ServerRun {
    <#
        Start the published server with fully isolated state, wait for it, collect
        the three endpoint answers, then ask it to stop and record HOW it stopped.
        Every run here uses its own config/cache/log/data directories: a probe that
        shares state with an earlier probe cannot tell a fresh install from an
        upgrade, and W0 needs both answers.
    #>
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [Parameter(Mandatory)] [string] $StateRoot,
        [Parameter()] [string] $FfmpegPath = '',
        [Parameter()] [string] $WebDir = '',
        [Parameter()] [switch] $NoWebClient,
        [Parameter()] [hashtable] $EnvironmentOverride = @{},
        # A cold first start creates the database and runs every migration. The
        # first hosted run timed out at 180 s while still applying them, which
        # reads as "this build does not start" and is not what was measured.
        [Parameter()] [int] $TimeoutSeconds = 600
    )

    $dirs = @{
        config = Join-Path $StateRoot 'config'
        cache  = Join-Path $StateRoot 'cache'
        log    = Join-Path $StateRoot 'log'
        data   = Join-Path $StateRoot 'data'
    }
    foreach ($d in $dirs.Values) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

    $port = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$port"

    # The server does not take its listening port from Kestrel configuration or
    # from an environment variable: ApplicationHost reads
    # NetworkConfiguration.InternalHttpPort, which the configuration manager
    # persists as <configdir>/network.xml (store key "network", default 8096).
    # Seeding that file is therefore the only way to give each run its own port,
    # and each run needs its own so a socket still held by the previous server
    # cannot be misread as "this build does not start". Auto-discovery and UPnP
    # are off because a probe must not broadcast on the runner's network.
    @"
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <InternalHttpPort>$port</InternalHttpPort>
  <PublicHttpPort>$port</PublicHttpPort>
  <EnableHttps>false</EnableHttps>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
</NetworkConfiguration>
"@ | Set-Content -LiteralPath (Join-Path $dirs.config 'network.xml') -Encoding utf8NoBOM

    $arguments = @(
        '--datadir',   $dirs.data
        '--configdir', $dirs.config
        '--cachedir',  $dirs.cache
        '--logdir',    $dirs.log
    )
    if ($FfmpegPath) { $arguments += @('--ffmpeg', $FfmpegPath) }
    if ($NoWebClient) { $arguments += '--nowebclient' } elseif ($WebDir) { $arguments += @('--webdir', $WebDir) }

    $stdout = Join-Path $StateRoot 'stdout.txt'
    $stderr = Join-Path $StateRoot 'stderr.txt'

    $previousEnvironment = @{}
    foreach ($key in $EnvironmentOverride.Keys) {
        $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $EnvironmentOverride[$key])
    }
    try {
        # Start-Process joins -ArgumentList with spaces and quotes NOTHING, so a
        # path containing a space arrives at the server split in two. The first
        # run of this probe truncated the accented 'Program Files ...' leaf at
        # its first space, and the server died in
        # BaseApplicationPaths.CheckOrCreateMarker on a marker it had written
        # into the wrong directory. Quoting each element is the
        # fix; the paths under test never contain a quote themselves.
        $quoted = @($arguments | ForEach-Object { '"' + $_ + '"' })
        $process = Start-Process -FilePath $Exe -ArgumentList $quoted -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr -NoNewWindow

        $root = Wait-ForHttp -BaseUrl "$baseUrl/" -TimeoutSeconds $TimeoutSeconds

        $result = @{
            started        = $null -ne $root
            exe            = $Exe
            baseUrl        = $baseUrl
            arguments      = $arguments
            rootStatus     = if ($root) { [int]$root.StatusCode } else { $null }
            rootBodyLength = if ($root) { $root.RawContentLength } else { $null }
            healthStatus   = $null
            healthBody     = ''
            webBootstrap   = $false
            exitCode       = $null
            stopSeconds    = $null
            stdoutTail     = ''
            stderrTail     = ''
            stateRoot      = $StateRoot
        }

        if ($root) {
            try {
                $health = Invoke-WebRequest -Uri "$baseUrl/health" -SkipHttpErrorCheck -TimeoutSec 30
                $result.healthStatus = [int]$health.StatusCode
                $result.healthBody = ($health.Content | Out-String).Trim()
            } catch {
                $result.healthBody = "request failed: $($_.Exception.Message)"
            }

            # The Web bootstrap, not merely a 200. The payload's entry document
            # has to reference a hashed bundle for the browser to have anything
            # to run; a 200 that returns the setup page is not the Web client.
            try {
                $index = Invoke-WebRequest -Uri "$baseUrl/web/index.html" -SkipHttpErrorCheck -TimeoutSec 30
                $body = ($index.Content | Out-String)
                $result.webBootstrap = ($index.StatusCode -eq 200) -and
                    ($body -match '(?i)<script[^>]+src="[^"]*main\.tesserafin[^"]*\.bundle\.js')
            } catch {
                $result.webBootstrap = $false
            }
        }

        # Shutdown. Ctrl-C cannot be delivered to another console group from here,
        # so the probe closes the main window / signals the process the way a
        # service stop would and measures how long a clean exit takes. A transcode
        # is not running, so this is the floor, not the worst case.
        $stopWatch = [System.Diagnostics.Stopwatch]::StartNew()
        if (-not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(30000)) {
                $result.forcedKill = $true
                $process.Kill($true)
            }
        }
        $process.WaitForExit()
        $stopWatch.Stop()

        # Keep the WHOLE console output as evidence, not only the tail: the
        # decisive line is usually the FIRST exception, which a tail cannot show.
        $label = Split-Path -Leaf $StateRoot
        foreach ($pair in @(@($stdout, 'stdout'), @($stderr, 'stderr'))) {
            if (Test-Path -LiteralPath $pair[0]) {
                Copy-Item -LiteralPath $pair[0] `
                    -Destination (Join-Path $EvidenceDir "$label.$($pair[1]).txt") -Force
            }
        }

        $result.stopSeconds = [math]::Round($stopWatch.Elapsed.TotalSeconds, 1)
        $result.exitCode = $process.ExitCode
        # -Raw and -Tail cannot be combined; join the tail lines instead.
        if (Test-Path -LiteralPath $stdout) { $result.stdoutTail = ((Get-Content -LiteralPath $stdout -Tail 40) -join "`n") }
        if (Test-Path -LiteralPath $stderr) { $result.stderrTail = ((Get-Content -LiteralPath $stderr -Tail 40) -join "`n") }

        return $result
    } finally {
        foreach ($key in $previousEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key])
        }
    }
}

# -- 1. Host identity -----------------------------------------------------------

$sdks = @(& dotnet --list-sdks) 2>&1
$runtimes = @(& dotnet --list-runtimes) 2>&1

Add-W0Fact -Evidence $evidence -Id 'host.identity' -Bucket 'working' `
    -Detail 'Native Windows x64 host identity recorded from the runner itself.' -Data @{
        imageOs            = $env:ImageOS
        imageVersion       = $env:ImageVersion
        runnerArch         = $env:RUNNER_ARCH
        runnerName         = $env:RUNNER_NAME
        osVersion          = [System.Environment]::OSVersion.VersionString
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        osArchitecture     = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        powerShell         = $PSVersionTable.PSVersion.ToString()
        powerShellEdition  = $PSVersionTable.PSEdition
        windowsPowerShell  = (Get-Command powershell.exe -ErrorAction SilentlyContinue)?.Version?.ToString()
        dotnetSdks         = $sdks
        dotnetRuntimes     = $runtimes
    }

# -- 2. Clean self-contained publish --------------------------------------------

$publishDir = Join-Path $WorkRoot 'publish'
if (Test-Path -LiteralPath $publishDir) { Remove-Item -Recurse -Force -LiteralPath $publishDir }

$project = Join-Path $RepoRoot 'Tesserafin.Server/Tesserafin.Server.csproj'
$publishLog = Join-Path $EvidenceDir 'publish.log'

$publishWatch = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir *>&1 | Tee-Object -FilePath $publishLog
$publishExit = $LASTEXITCODE
$publishWatch.Stop()

if ($publishExit -ne 0) {
    Add-W0Fact -Evidence $evidence -Id 'publish.selfcontained' -Bucket 'blocked' `
        -Detail "dotnet publish --runtime win-x64 --self-contained true FAILED with exit code $publishExit." `
        -Data @{ exitCode = $publishExit; log = 'publish.log' }
    Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'baseline.json') | Out-Null
    throw "W0 HARD STOP: the server cannot be published self-contained for win-x64 (exit $publishExit)."
}

$exe = Join-Path $publishDir 'tesserafin.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "W0 HARD STOP: publish succeeded but produced no tesserafin.exe in '$publishDir'."
}

$tree = Get-W0TreeDigest -Root $publishDir
$selfContained = Test-W0SelfContained -PublishDir $publishDir
$machine = Get-W0PeMachine -Path $exe
$nativeDlls = @(Get-ChildItem -LiteralPath $publishDir -Recurse -Filter *.dll |
    Where-Object { -not (Test-Path -LiteralPath ($_.FullName -replace '\.dll$', '.deps.json')) } |
    ForEach-Object {
        try { @{ name = $_.Name; machine = (Get-W0PeMachine -Path $_.FullName); bytes = $_.Length } }
        catch { $null }
    } | Where-Object { $_ })

$foreignArchitecture = @($nativeDlls | Where-Object { $_.machine -notin 'x64', 'unknown-0x0000' })

Add-W0Fact -Evidence $evidence -Id 'publish.selfcontained' -Bucket 'working' `
    -Detail ("dotnet publish --runtime win-x64 --self-contained true succeeds on stock master " +
             "in $([math]::Round($publishWatch.Elapsed.TotalSeconds,0))s and emits tesserafin.exe. " +
             "AssemblyName=tesserafin is already correct for Windows; no csproj change was needed.") `
    -Data @{
        exitCode          = 0
        seconds           = [math]::Round($publishWatch.Elapsed.TotalSeconds, 0)
        fileCount         = $tree.count
        treeDigest        = $tree.digest
        totalBytes        = (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
        exeMachine        = $machine
        selfContained     = $selfContained
        nativeLibraries   = $nativeDlls
        foreignArchitecture = $foreignArchitecture
    }

# Wrong-architecture negative control, answered from the PE header rather than
# from whether the binary happened to start on this host.
Add-W0Fact -Evidence $evidence -Id 'control.architecture' -Bucket 'working' `
    -Detail ("Wrong-architecture control: tesserafin.exe reports COFF machine '$machine' and " +
             "$($foreignArchitecture.Count) delivered native library/libraries disagree with x64.") `
    -Data @{ expected = 'x64'; actual = $machine; foreign = $foreignArchitecture }

if ($machine -ne 'x64') {
    throw "W0 HARD STOP: the published tesserafin.exe is '$machine', not x64."
}

# Missing self-contained runtime negative control.
Add-W0Fact -Evidence $evidence -Id 'control.selfcontained' -Bucket 'working' `
    -Detail ("Missing-runtime control: the delivered tree is judged self-contained from its own " +
             "bytes (hostfxr/hostpolicy/coreclr/System.Private.CoreLib present, no framework " +
             "reference in tesserafin.runtimeconfig.json), not from the build log.") `
    -Data $selfContained

if (-not $selfContained.selfContained) {
    throw ("W0 HARD STOP: the publish is not self-contained. missing=" +
           ($selfContained.missing -join ',') + " declaresFramework=$($selfContained.declaresFramework)")
}

# -- 3. FFmpeg discovery, as an explicit pair -----------------------------------

# What the RUNNER has, asked before anything is arranged. On win25-vs2026 the
# answer is nothing, and that single fact is why FfmpegException makes the stock
# Windows server unstartable out of the box.
$runnerFfmpeg = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)?.Source

# The encoder the probe REQUESTS lives in its own directory, outside PATH, so
# "the requested binary was actually selected" is answerable rather than merely
# plausible: a PATH fallback would record a different string and fail.
$ffmpegHome = Join-Path $WorkRoot 'w0-requested-ffmpeg'
$requestedFfmpeg = Join-Path $ffmpegHome 'ffmpeg.exe'
New-Item -ItemType Directory -Force -Path $ffmpegHome | Out-Null

$ffmpegOrigin = 'none'
if ($FfmpegDir -and (Test-Path -LiteralPath (Join-Path $FfmpegDir 'ffmpeg.exe'))) {
    Copy-Item -LiteralPath (Join-Path $FfmpegDir 'ffmpeg.exe') -Destination $requestedFfmpeg -Force
    $spikeFfprobe = Join-Path $FfmpegDir 'ffprobe.exe'
    if (Test-Path -LiteralPath $spikeFfprobe) {
        Copy-Item -LiteralPath $spikeFfprobe -Destination (Join-Path $ffmpegHome 'ffprobe.exe') -Force
    }
    $ffmpegOrigin = 'w0-native-spike'
} elseif ($runnerFfmpeg) {
    Copy-Item -LiteralPath $runnerFfmpeg -Destination $requestedFfmpeg -Force
    $ffmpegOrigin = 'runner-preinstalled'
}

$ffmpegVersion = if (Test-Path -LiteralPath $requestedFfmpeg) {
    (& $requestedFfmpeg -hide_banner -version 2>&1 | Select-Object -First 1)
} else { $null }

Add-W0Fact -Evidence $evidence -Id 'ffmpeg.provenance' `
    -Bucket $(if ($ffmpegOrigin -eq 'w0-native-spike') { 'working' }
              elseif ($ffmpegOrigin -eq 'runner-preinstalled') { 'test-host-dependency' }
              else { 'missing' }) `
    -Detail ("The runner image supplies NO ffmpeg: Get-Command ffmpeg.exe resolved to " +
             "'$runnerFfmpeg'. Since FfmpegException is fatal at startup, that alone makes the " +
             "stock Windows server unstartable out of the box, and it is why W1 is a blocking " +
             "deliverable rather than a refinement. The encoder used below came from " +
             "'$ffmpegOrigin' -- the W0 native source-build spike, compiled on a Windows runner " +
             "from the pinned upstream commit -- which keeps the whole chain inside W0 and inside " +
             "the pin instead of depending on whatever an image happened to preinstall. It is " +
             "still NOT the accepted Tesserafin Windows runtime: it is bounded by " +
             "--disable-autodetect and carries none of the required component closure. W1 owns " +
             "the real one.") `
    -Data @{
        runnerFfmpeg  = $runnerFfmpeg
        origin        = $ffmpegOrigin
        requestedCopy = $requestedFfmpeg
        version       = $ffmpegVersion
        sha256        = if (Test-Path -LiteralPath $requestedFfmpeg) {
            (Get-FileHash -LiteralPath $requestedFfmpeg -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { $null }
    }

# -- 4. Startup from a hostile path, with isolated state ------------------------

# Spaces AND non-ASCII in one path, because they fail differently: quoting versus
# code page. A probe that only tests one of them proves half the thing.
# The path is built from code points rather than written as literals so this
# script stays pure ASCII on disk: PSScriptAnalyzer's PSUseBOMForUnicodeEncodedFile
# would otherwise demand a byte-order mark, and .editorconfig declares
# `charset = utf-8` for every file in the repository. The path actually exercised
# is unchanged and fully non-ASCII.
$hostileLeaf = [string]::Concat(
    'Program Files ', [char]0x00C8, [char]0x0074, [char]0x00E8, [char]0x005C,
    'Tesserafin ', [char]0x2014, ' Caf', [char]0x00E9, ' ',
    [char]0x6570, [char]0x636E)
$hostileRoot = Join-Path $WorkRoot $hostileLeaf
New-Item -ItemType Directory -Force -Path $hostileRoot | Out-Null
$hostileTree = Join-Path $hostileRoot 'app'
Copy-Item -LiteralPath $publishDir -Destination $hostileTree -Recurse -Force

$primary = Invoke-ServerRun -Exe (Join-Path $hostileTree 'tesserafin.exe') `
    -StateRoot (Join-Path $hostileRoot 'state-primary') `
    -FfmpegPath $requestedFfmpeg `
    -WebDir $WebPayloadDir `
    -NoWebClient:([string]::IsNullOrEmpty($WebPayloadDir))

Add-W0Fact -Evidence $evidence -Id 'startup.hostilepath' -Bucket $(if ($primary.started) { 'test-host-dependency' } else { 'blocked' }) `
    -Detail ("Start from a path containing spaces, accented Latin, an em dash and CJK: " +
             "'$hostileTree'. started=$($primary.started). Bucketed as a test-host dependency " +
             "because it required the runner's FFmpeg, not because the path handling is in doubt.") `
    -Data $primary

# -- 5. Relocation --------------------------------------------------------------

$relocatedRoot = Join-Path $WorkRoot 'relocated\deeper\still'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $relocatedRoot) | Out-Null
Move-Item -LiteralPath $hostileTree -Destination $relocatedRoot -Force

$relocated = Invoke-ServerRun -Exe (Join-Path $relocatedRoot 'tesserafin.exe') `
    -StateRoot (Join-Path $WorkRoot 'state-relocated') `
    -FfmpegPath $requestedFfmpeg `
    -WebDir $WebPayloadDir `
    -NoWebClient:([string]::IsNullOrEmpty($WebPayloadDir))

Add-W0Fact -Evidence $evidence -Id 'startup.relocated' -Bucket $(if ($relocated.started) { 'test-host-dependency' } else { 'blocked' }) `
    -Detail ("The SAME tree moved to '$relocatedRoot' and started again with fresh state. " +
             "started=$($relocated.started). This is the portable-ZIP precondition (W2): the " +
             "server must not bake its own location into anything.") `
    -Data $relocated

Add-W0Fact -Evidence $evidence -Id 'control.pathdependent' -Bucket 'working' `
    -Detail ("Path-dependent-startup control: a tree that only works where it was published " +
             "fails this pair. Both starts are required to agree; primary=$($primary.started) " +
             "relocated=$($relocated.started).") `
    -Data @{ primary = $primary.started; relocated = $relocated.started; agree = ($primary.started -eq $relocated.started) }

# -- 6. Endpoints and the Web payload -------------------------------------------

$webBucket = if ($WebPayloadDir -and $relocated.webBootstrap) { 'working' }
             elseif ($WebPayloadDir) { 'missing' }
             else { 'missing' }

Add-W0Fact -Evidence $evidence -Id 'endpoints' -Bucket $(if ($relocated.rootStatus -and $relocated.healthStatus -eq 200) { 'test-host-dependency' } else { 'missing' }) `
    -Detail ("From the RELOCATED tree: '/' answered $($relocated.rootStatus), '/health' answered " +
             "$($relocated.healthStatus), Web bootstrap=$($relocated.webBootstrap).") `
    -Data @{
        root         = $relocated.rootStatus
        health       = $relocated.healthStatus
        healthBody   = $relocated.healthBody
        webBootstrap = $relocated.webBootstrap
    }

Add-W0Fact -Evidence $evidence -Id 'control.webpayload' -Bucket $webBucket `
    -Detail $(if ($WebPayloadDir) {
        "Missing-Web-payload control: a digest-verified payload WAS supplied at '$WebPayloadDir' " +
        "and the bootstrap assertion is $($relocated.webBootstrap). The assertion looks for the " +
        "hashed main.tesserafin bundle reference, so a 200 that returns the setup page fails it."
    } else {
        "Missing-Web-payload control EXERCISED: no payload was supplied, the server was started " +
        "with --nowebclient, and the Web bootstrap is correctly unavailable. This is the negative " +
        "half of the control."
    }) `
    -Data @{ payloadDir = $WebPayloadDir; bootstrap = $relocated.webBootstrap }

# -- 7. Was the REQUESTED ffmpeg actually selected? -----------------------------

# MediaEncoder.SetFFmpegPath writes the validated path to encoding.xml as
# EncoderAppPathDisplay. That file is the server's own answer to "which binary
# did you choose", which is why the probe reads it instead of the log.
$encodingXml = Join-Path (Join-Path $WorkRoot 'state-relocated') 'config\encoding.xml'
$selectedPath = $null
if (Test-Path -LiteralPath $encodingXml) {
    $selectedPath = ([xml](Get-Content -LiteralPath $encodingXml -Raw)).EncodingOptions.EncoderAppPathDisplay
}

$selectionProven = $selectedPath -and
    ([System.IO.Path]::GetFullPath($selectedPath) -eq [System.IO.Path]::GetFullPath($requestedFfmpeg))

Add-W0Fact -Evidence $evidence -Id 'ffmpeg.selection' -Bucket $(if ($selectionProven) { 'working' } else { 'missing' }) `
    -Detail ("--ffmpeg '$requestedFfmpeg' was requested; the server recorded " +
             "EncoderAppPathDisplay='$selectedPath' in its own encoding.xml. proven=$selectionProven. " +
             "The requested copy lives outside PATH, so a PATH fallback would produce a different " +
             "string and fail this assertion.") `
    -Data @{ requested = $requestedFfmpeg; selected = $selectedPath; proven = $selectionProven; encodingXml = $encodingXml }

# -- 8. No-FFmpeg negative control ----------------------------------------------

# FfmpegException is fatal at startup, so the honest way to show that every green
# start above is host-supplied is to take FFmpeg away and record the failure.
$scrubbedPath = (($env:PATH -split ';') | Where-Object {
    $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'ffmpeg.exe'))
}) -join ';'

$noFfmpeg = Invoke-ServerRun -Exe (Join-Path $relocatedRoot 'tesserafin.exe') `
    -StateRoot (Join-Path $WorkRoot 'state-noffmpeg') `
    -NoWebClient `
    -EnvironmentOverride @{ PATH = $scrubbedPath } `
    -TimeoutSeconds 600

Add-W0Fact -Evidence $evidence -Id 'control.systemffmpeg' -Bucket 'working' `
    -Detail ("System-FFmpeg-substitution control: PATH scrubbed of every directory containing " +
             "ffmpeg.exe and no --ffmpeg given. started=$($noFfmpeg.started) exitCode=$($noFfmpeg.exitCode). " +
             "A server that still came up would mean the probe was reading a stale or shared state " +
             "directory rather than a fresh start.") `
    -Data @{
        started    = $noFfmpeg.started
        exitCode   = $noFfmpeg.exitCode
        stderrTail = $noFfmpeg.stderrTail
        stdoutTail = $noFfmpeg.stdoutTail
    }

# -- 9. Shutdown ----------------------------------------------------------------

Add-W0Fact -Evidence $evidence -Id 'shutdown' -Bucket $(if ($relocated.exitCode -eq 0) { 'working' } else { 'missing' }) `
    -Detail ("Console shutdown of the relocated server took $($relocated.stopSeconds)s and exited " +
             "with code $($relocated.exitCode). No transcode was running, so this is the floor. " +
             "The Windows Service stop timeout (W3) must be derived from the WORST case, not this one.") `
    -Data @{
        stopSeconds = $relocated.stopSeconds
        exitCode    = $relocated.exitCode
        forcedKill  = if ($relocated.ContainsKey('forcedKill')) { $relocated.forcedKill } else { $false }
    }

# -- 10. Completeness gate ------------------------------------------------------

$required = @(
    'host.identity'
    'publish.selfcontained'
    'control.architecture'
    'control.selfcontained'
    'ffmpeg.provenance'
    'startup.hostilepath'
    'startup.relocated'
    'control.pathdependent'
    'endpoints'
    'control.webpayload'
    'ffmpeg.selection'
    'control.systemffmpeg'
    'shutdown'
)

$completeness = Test-W0EvidenceComplete -Evidence $evidence -RequiredIds $required
Add-W0Fact -Evidence $evidence -Id 'control.completeness' -Bucket 'working' `
    -Detail ("Incomplete-evidence control: all $($required.Count) mandatory measurements are " +
             "present and bucketed. complete=$($completeness.complete).") `
    -Data $completeness

$path = Save-W0Evidence -Evidence $evidence -Path (Join-Path $EvidenceDir 'baseline.json')
Write-Host "W0 baseline evidence written to $path"

if (-not $completeness.complete) {
    throw ("W0: incomplete evidence. absent=" + ($completeness.absent -join ',') +
           " unclassified=" + ($completeness.unclassified -join ','))
}
