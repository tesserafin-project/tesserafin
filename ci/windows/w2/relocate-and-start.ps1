#Requires -Version 7.2
<#
.SYNOPSIS
    Start the portable win-x64 Tesserafin server ZIP from a hostile path, move
    the SAME tree to a different depth, and start it again -- refusing, in every
    way the W0 probe learned to refuse, to call a server that did not start
    "started".

.DESCRIPTION
    W2-A3 (#256). `docs/distribution/W0-windows-server.md` §6 requires the
    portable ZIP to be "**Relocatable.** Proven by moving the tree and starting
    it again (§2.3); the server must bake its own location into nothing", and to
    ship "**no** state. Configuration, database, cache and logs are always given
    by argument".

    This script proves exactly that, and nothing else. It does not assemble a
    second time, it does not compare two runners, it registers no service, it
    publishes nothing and it uploads nothing.

    The archive under test is produced HERE, in this job, by the FROZEN W2-A2
    assembler:

        ci/windows/w2/assemble-server-zip.ps1

    which in turn drives the frozen W2-A0 Web consumer and the frozen W1/W2-A1
    FFmpeg consumer. This script authors no acquisition of its own and takes no
    archive from an Actions artifact, from a previous run or from a registry:
    W0 §8.7 forbids the artifact handover in production, and an archive that
    arrived from somewhere else is not the archive this commit builds. That is
    also why `ci/windows/w2/start-controls.py` asserts this file grows no
    `-Reference`, `-RunId`, `-ArchivePath` or `-Tag`.

    ── The four readiness traps this inherits from W0 §2.3 ────────────────────

    W0 walked into all four, and each one made a FAILING server look like a
    starting one or the reverse. They are implemented here, not restated:

      1. THE PORT IS NOT IN CONFIGURATION OR THE ENVIRONMENT. `ApplicationHost`
         reads `NetworkConfiguration.InternalHttpPort`, persisted by the
         configuration manager, and the server logs only "Kestrel is listening
         on 0.0.0.0" with no port in it. Seeding that file is a REQUEST, not a
         guarantee. So the listening port is read from the PROCESS's own TCP
         ports, and when the process bound none this script REFUSES rather than
         falling back to what the file asked for, to an environment variable or
         to the compiled-in default. W0 recorded a run that logged "Startup
         complete" in 22 s as "did not start" for exactly this reason.

      2. A REDIRECT IS AN ANSWER. `Invoke-WebRequest -MaximumRedirection 0`
         raises on a 3xx and `-SkipHttpErrorCheck` does not cover that class,
         while `/` redirects to the web client -- so every run looked dead.
         `HttpClient` with `AllowAutoRedirect` disabled returns the 302 as a
         value, which is the behaviour a probe needs.

      3. 503 FROM THE SetupServer IS NOT READY. The startup `SetupServer` binds
         the real port early and answers EVERY path with 503 and
         {"status":"starting",...}. Readiness is "`/` answers with something
         other than 503". Requiring 200 would be wrong in the other direction:
         that asserts a routing decision rather than liveness.

      4. A LIVE PROCESS IS PART OF READINESS. Even the 503 rule was not enough.
         The setup server answered non-503 in the same instant the application
         host was tearing itself down over `FfmpegException`, and W0's
         no-FFmpeg negative control reported a STARTED server. So the process
         must still be running three seconds after it answers.

    ── What "the same tree, relocated" means here ─────────────────────────────

    The archive is extracted ONCE. The first start runs from a directory whose
    name carries spaces, accented Latin, an em dash and CJK. The second start
    runs from the SAME directory tree, moved to a different depth -- moved, not
    extracted again, and the identity of `tesserafin.exe` is hashed on both
    sides of the move to say so. Both starts use their own
    `--datadir --configdir --cachedir --logdir`, because a proof that shares
    state cannot tell a fresh install from an upgrade, and both point
    `--webdir` and `--ffmpeg` INSIDE the relocated tree, because a start that
    borrows either from the build tree has not proven the package portable.

    Fail-closed. Every one of these produces a named refusal and no evidence
    document:

      * the assembler produced no archive, or more than one;
      * the archive extracted anything other than one top-level directory;
      * a start was launched from a path inside the assembler's work directory;
      * the two starts were given the same state directory;
      * the process bound no listening TCP port;
      * `/` never left the 503 SetupServer inside the budget;
      * the process was gone three seconds after answering;
      * the entry document did not reference the hashed
        `main.tesserafin...bundle.js`;
      * the second start ran from a re-extracted tree rather than the moved one.

.PARAMETER RepoRoot
    The checkout being proven. The frozen assembler is read from here.

.PARAMETER WorkDir
    Private scratch. It must not already exist or must be empty: a second proof
    that inherited the first one's extracted tree would prove nothing about
    relocation.

.PARAMETER OrasPath
    The pinned ORAS client the frozen FFmpeg consumer needs.

.PARAMETER PythonPath
    The interpreter the frozen consumers use. Passed straight through.

.PARAMETER SourceDateEpoch
    Seconds since the Unix epoch, derived from the commit being built. Required
    by the frozen assembler and never defaulted here either.

.PARAMETER EvidencePath
    Where the machine-readable record of both starts is written. It is written
    only when both starts passed every check above.

.PARAMETER Oracle
    ORACLE ONLY. Drive ONE decision function -- readiness, port resolution or
    the entry-document assertion -- against a caller-supplied fixture, so
    `start-controls.py` can observe the REAL rules refusing the REAL failure
    shapes on a host with no server and no network. It assembles nothing,
    starts nothing and writes nothing. It is modelled on the frozen assembler's
    own `-StageRoot` pack-only parameter set, and `start-controls.py` (S13)
    asserts the production workflow never passes it.
#>

[CmdletBinding(DefaultParameterSetName = 'Prove')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Prove')]
    [string] $RepoRoot,

    [Parameter(Mandatory = $true, ParameterSetName = 'Prove')]
    [string] $WorkDir,

    [Parameter(Mandatory = $true, ParameterSetName = 'Prove')]
    [string] $OrasPath,

    [Parameter(ParameterSetName = 'Prove')]
    [string] $PythonPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Prove')]
    [string] $EvidencePath,

    # Deliberately NOT Mandatory, for the reason the frozen assembler gives: a
    # mandatory parameter that is omitted produces PowerShell's own prompt or a
    # generic binding error, and "the epoch was missing" has to be sayable out
    # loud.
    [Parameter(ParameterSetName = 'Prove')]
    [int64] $SourceDateEpoch = 0,

    # A cold FIRST start creates the database and applies every migration. W0's
    # first hosted run timed out at 180 s while migrations were still running
    # and read as "this build does not start", which is not what was measured.
    [Parameter(ParameterSetName = 'Prove')]
    [int] $ReadyTimeoutSeconds = 600,

    [Parameter(Mandatory = $true, ParameterSetName = 'Oracle')]
    [ValidateSet('readiness', 'port', 'bundle')]
    [string] $Oracle,

    [Parameter(ParameterSetName = 'Oracle')]
    [string] $BaseUrl,

    [Parameter(ParameterSetName = 'Oracle')]
    [int] $OracleProcessId = 0,

    # An empty string means "the process bound nothing", which is the shape the
    # port rule has to refuse rather than paper over.
    [Parameter(ParameterSetName = 'Oracle')]
    [AllowEmptyString()]
    [string] $OracleListeningPorts,

    [Parameter(ParameterSetName = 'Oracle')]
    [int] $OracleTimeoutSeconds = 3
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# The relative layout the frozen W2-A2 assembler stages, mirroring W0 §9.1.
# These are READ from the extracted package; nothing here creates them.
# ---------------------------------------------------------------------------
$WEB_SUBDIR = 'web'
$FFMPEG_RELATIVE_EXE = 'ffmpeg/bin/ffmpeg.exe'

# W0 §2.4: "The Web bootstrap assertion is deliberately stricter than a 200: the
# entry document must reference the hashed `main.tesserafin...bundle.js`, so a
# 200 that returns the setup page fails it."
$ENTRY_DOCUMENT = 'web/index.html'
$BUNDLE_PATTERN = '(?i)<script[^>]+src="[^"]*main\.tesserafin[^"]*\.bundle\.js'

# W0 §2.3 trap 4: readiness is not a single sample.
$LIVENESS_SECONDS = 3

# ---------------------------------------------------------------------------

function Deny {
    param([string] $Category, [string] $Message)
    throw ("W2-A3 DENY [{0}] {1}" -f $Category, $Message)
}

function Write-Note {
    param([string] $Message)
    Write-Host ("W2-A3: {0}" -f $Message)
}

function Get-Sha256 {
    param([string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# The hostile leaf, built from code points rather than written as literals, so
# that the exact code points under test are stated in the source and cannot be
# silently normalised by an editor, a checkout or a console code page. A
# hostile-path proof whose hostile characters were quietly replaced proves
# nothing and still looks green. W0's probe builds its leaf the same way. The
# path actually exercised is fully non-ASCII:
#
#   spaces         -- quoting, and Start-Process's argument joining
#   accented Latin -- the console code page
#   em dash        -- a non-Latin-1 punctuation code point
#   CJK            -- outside any single-byte code page at all
#
# They fail differently, so a proof that tests only one of them proves a quarter
# of the thing.
function Get-HostileLeaf {
    return [string]::Concat(
        'Caf', [char]0x00E9, ' ', [char]0x2014, ' ',
        [char]0x76EE, [char]0x5F55, ' srv')
}

function Get-FreeTcpPort {
    <#
        A free port to REQUEST. This is never the port the proof then talks to:
        see Resolve-ServerPort. It exists so the two starts ask for different
        ports and a socket still held by the first server can never be misread
        as "the second start did not come up".
    #>
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

# ===========================================================================
# The three decision functions. Each is driven by the production path below AND
# by the -Oracle parameter set, so `start-controls.py` observes the REAL rule
# refusing the REAL failure shape rather than a second copy written for a test.
# ===========================================================================

function Get-HttpResponse {
    <#
        W0 §2.3 trap 2. `Invoke-WebRequest -MaximumRedirection 0` raises
        "maximum redirection count exceeded" on a 3xx -- a class
        `-SkipHttpErrorCheck` does NOT cover -- and `/` redirects to the web
        client, so a perfectly healthy server looked identical to one that was
        not answering at all. That cost W0 two hosted runs. `HttpClient` with
        `AllowAutoRedirect` disabled returns the 302 as a VALUE.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Uri,
        [int] $TimeoutSeconds = 15
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    try {
        $response = $client.GetAsync($Uri).GetAwaiter().GetResult()
        return @{
            status = [int]$response.StatusCode
            body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            location = if ($response.Headers.Location) { $response.Headers.Location.ToString() } else { '' }
        }
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Get-ProcessListeningPorts {
    <#
        What the PROCESS bound. Not what a file asked for, not what an
        environment variable said, not the compiled-in default.

        `Get-NetTCPConnection` is the only instrument on Windows that answers
        "which listening sockets belong to THIS process id". If it is not
        present the proof refuses: a fallback to any other source is the exact
        defect this function exists to remove.
    #>
    param([Parameter(Mandatory = $true)] [int] $ServerProcessId)

    if (-not (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
        Deny 'port' ('this host has no Get-NetTCPConnection, so the listening port cannot be ' +
            'read from the process. There is deliberately no other source: reading the port ' +
            'from anywhere else is what W0 measured as a false "did not start".')
    }
    return @(Get-NetTCPConnection -OwningProcess $ServerProcessId -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty LocalPort -Unique | Sort-Object)
}

function Resolve-ServerPort {
    <#
        W0 §2.3 trap 1, and the ruling's control 14.

        The rule is NOT "prefer the process and fall back to the file". A
        fallback is indistinguishable from never having asked the process: the
        run goes green on whatever the file said, and a server listening
        somewhere else is recorded as healthy. So an empty answer is a REFUSAL.

        `-PortsOverride` is ORACLE ONLY, and exists so a control can observe
        this refusal without a server. A switch guards it rather than a nullable
        array because PowerShell compares an empty array to $null as an empty
        array, and "no ports were supplied" and "no ports exist" are the two
        cases this function has to tell apart.
    #>
    param(
        [int] $ServerProcessId = 0,
        [int] $TimeoutSeconds = 300,
        [System.Diagnostics.Process] $Process = $null,
        [switch] $UsePortsOverride,
        [int[]] $PortsOverride = @()
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        # Wrapped in @() at the ASSIGNMENT, not only inside the branches: an
        # `if` used as an expression unrolls a one-element array to a scalar,
        # and `.Count` on that scalar throws under Set-StrictMode 3.0. A single
        # bound port is the common case, so the unwrapped form fails exactly
        # where it matters.
        $ports = @(if ($UsePortsOverride) { $PortsOverride }
                   else { Get-ProcessListeningPorts -ServerProcessId $ServerProcessId })
        if ($ports.Count -gt 0) { return [int]$ports[0] }
        if ($Process -and $Process.HasExited) {
            Deny 'port' ("the server process exited with $($Process.ExitCode) without binding a " +
                'listening TCP port')
        }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Milliseconds 750
    }
    Deny 'port' ('the server bound no listening TCP port within the budget. The port is NEVER ' +
        'taken from the network configuration file, from the environment or from the ' +
        'compiled-in default instead: seeding that file is a request and not a guarantee, and ' +
        'a proof that answers from the request rather than from the process cannot tell a ' +
        'server that did not start from one it knocked on the wrong door for.')
}

function Wait-ForReady {
    <#
        W0 §2.3 traps 3 and 4, together, because separately neither is enough.

        Readiness is "`/` answers with something OTHER than 503", AND the
        process is still running $LIVENESS_SECONDS after it answers. The setup
        server answered non-503 in the same instant the application host was
        disposing over FfmpegException, and W0's no-FFmpeg control reported a
        started server while its own log showed the host tearing down.

        Returns the answer, or $null with the reason on the host.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $BaseUri,
        [int] $TimeoutSeconds = 600,
        [System.Diagnostics.Process] $Process = $null,
        [int] $ProbeProcessId = 0
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastSetupBody = ''
    $lastTransportError = ''
    $answer = $null

    while ((Get-Date) -lt $deadline) {
        if ($Process -and $Process.HasExited) {
            Write-Note ("the server process exited with $($Process.ExitCode) before answering")
            return $null
        }
        try {
            $response = Get-HttpResponse -Uri $BaseUri -TimeoutSeconds 10
            if ($response.status -ne 503) { $answer = $response; break }
            $lastSetupBody = $response.body
        } catch {
            $lastTransportError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 750
    }

    if (-not $answer) {
        Write-Note ("'$BaseUri' never left the startup SetupServer inside ${TimeoutSeconds}s. " +
            "last 503 body: '$lastSetupBody'; last transport error: '$lastTransportError'")
        return $null
    }

    # Answering once is not surviving.
    Start-Sleep -Seconds $LIVENESS_SECONDS
    $alive = $true
    if ($Process) {
        $alive = -not $Process.HasExited
        if (-not $alive) {
            Write-Note ("'$BaseUri' answered $($answer.status) but the process exited with " +
                "$($Process.ExitCode) within ${LIVENESS_SECONDS}s; not started")
        }
    } elseif ($ProbeProcessId -gt 0) {
        $alive = $null -ne (Get-Process -Id $ProbeProcessId -ErrorAction SilentlyContinue)
        if (-not $alive) {
            Write-Note ("'$BaseUri' answered $($answer.status) but process $ProbeProcessId was " +
                "gone within ${LIVENESS_SECONDS}s; not started")
        }
    }
    if (-not $alive) { return $null }

    $answer.livenessSeconds = $LIVENESS_SECONDS
    return $answer
}

function Test-EntryDocument {
    <#
        W0 §2.4. Stricter than a 200 on purpose: the entry document must
        reference the hashed bundle, so a 200 that returns the setup page --
        or a server started with --nowebclient, or one pointed at an empty
        --webdir -- fails it.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $BaseUri
    )

    $uri = $BaseUri.TrimEnd('/') + '/' + $ENTRY_DOCUMENT
    try {
        $response = Get-HttpResponse -Uri $uri -TimeoutSeconds 30
    } catch {
        return @{ ok = $false; status = $null; reference = ''
                  reason = "request failed: $($_.Exception.Message)"; uri = $uri }
    }
    if ($response.status -ne 200) {
        return @{ ok = $false; status = $response.status; reference = ''
                  reason = "the entry document answered $($response.status)"; uri = $uri }
    }
    if ($response.body -notmatch $BUNDLE_PATTERN) {
        return @{ ok = $false; status = 200; reference = ''
                  reason = ('the entry document answered 200 but references no hashed ' +
                            'main.tesserafin bundle, so it is not the Web client')
                  uri = $uri }
    }
    $matched = [regex]::Match($response.body, $BUNDLE_PATTERN).Value
    return @{ ok = $true; status = 200; reason = ''; uri = $uri; reference = $matched }
}

# ===========================================================================
# One start
# ===========================================================================

function Show-ServerOutput {
    <#
        The HEAD of the console output, not the tail. The decisive line is the
        FIRST exception -- FfmpegException, a marker-file sanity check, a bind
        failure -- and a tail cannot show it. This is printed on every refusal
        so a red hosted run is diagnosable without a second push.
    #>
    param([string] $StateRoot)
    foreach ($leaf in @('stdout.txt', 'stderr.txt')) {
        $path = Join-Path $StateRoot $leaf
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $head = @(Get-Content -LiteralPath $path -TotalCount 60 -ErrorAction SilentlyContinue)
        if ($head.Count -eq 0) { continue }
        Write-Host ("---- $leaf (first $($head.Count) lines) ----")
        $head | ForEach-Object { Write-Host $_ }
        Write-Host '---- end ----'
    }
}

function Invoke-ServerStart {
    <#
        Start the relocated tree with fully isolated state, discover the port
        from the process, wait for readiness, assert the entry document, then
        stop.

        Stopping is not what this slice proves and is not measured as a graceful
        shutdown: W0 measured that a console process started with -NoNewWindow
        has no main window, so CloseMainWindow is a no-op and every stop
        degrades into a kill after the timeout. The tree is killed and waited
        for, which is what the second start needs and all it needs.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Label,
        [Parameter(Mandatory = $true)] [string] $PackageRoot,
        [Parameter(Mandatory = $true)] [string] $StateRoot,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $exe = Join-Path $PackageRoot 'tesserafin.exe'
    $webDir = Join-Path $PackageRoot $WEB_SUBDIR
    $ffmpeg = Join-Path $PackageRoot ($FFMPEG_RELATIVE_EXE -replace '/', [System.IO.Path]::DirectorySeparatorChar)

    foreach ($required in @($exe, $ffmpeg)) {
        if (-not [System.IO.File]::Exists($required)) {
            Deny 'relocated-tree' ("the relocated package carries no '$required'")
        }
    }
    if (-not [System.IO.Directory]::Exists($webDir)) {
        Deny 'relocated-tree' ("the relocated package carries no '$webDir'")
    }

    $dirs = [ordered]@{
        config = Join-Path $StateRoot 'config'
        data = Join-Path $StateRoot 'data'
        cache = Join-Path $StateRoot 'cache'
        log = Join-Path $StateRoot 'log'
    }
    foreach ($dir in $dirs.Values) { $null = New-Item -ItemType Directory -Force -Path $dir }

    # A port to REQUEST, so the two starts do not contend for one socket. What
    # this proof then talks to comes from Resolve-ServerPort and from nowhere
    # else; the requested value is recorded as evidence and never used to build
    # a URL. W0 §2.3: "Seeding that file is a request rather than a guarantee".
    $requestedPort = Get-FreeTcpPort
    $networkConfiguration = @"
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <InternalHttpPort>$requestedPort</InternalHttpPort>
  <PublicHttpPort>$requestedPort</PublicHttpPort>
  <EnableHttps>false</EnableHttps>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
</NetworkConfiguration>
"@
    Set-Content -LiteralPath (Join-Path $dirs.config 'network.xml') `
        -Value $networkConfiguration -Encoding utf8NoBOM

    $arguments = @(
        '--datadir', $dirs.data
        '--configdir', $dirs.config
        '--cachedir', $dirs.cache
        '--logdir', $dirs.log
        '--webdir', $webDir
        '--ffmpeg', $ffmpeg
    )

    $stdout = Join-Path $StateRoot 'stdout.txt'
    $stderr = Join-Path $StateRoot 'stderr.txt'

    # Start-Process joins -ArgumentList with spaces and quotes NOTHING, so a
    # path containing a space arrives at the server split in two. W0's first run
    # truncated the accented leaf at its first space and the server died in
    # BaseApplicationPaths.CheckOrCreateMarker on a marker written into the
    # wrong directory. Every element is quoted; none of these paths contains a
    # quote itself.
    $quoted = @($arguments | ForEach-Object { '"' + $_ + '"' })
    Write-Note ("$Label : starting '$exe'")
    Write-Note ("$Label : state '$StateRoot', requested port $requestedPort")
    $process = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -NoNewWindow

    $result = [ordered]@{
        label = $Label
        exe = $exe
        webDir = $webDir
        ffmpeg = $ffmpeg
        stateRoot = $StateRoot
        stateDirectories = @($dirs.Values)
        arguments = $arguments
        requestedPort = $requestedPort
        discoveredPort = $null
        listeningPorts = @()
        processPath = ''
        rootStatus = $null
        rootLocation = ''
        livenessSeconds = $LIVENESS_SECONDS
        entryDocumentStatus = $null
        entryDocumentReference = ''
        started = $false
    }

    try {
        # The exe that is RUNNING, asked of the process rather than assumed from
        # what was launched.
        try { $result.processPath = [string]$process.MainModule.FileName } catch { $result.processPath = '' }

        $port = Resolve-ServerPort -ServerProcessId $process.Id -Process $process `
            -TimeoutSeconds ([Math]::Min(300, $TimeoutSeconds))
        $result.discoveredPort = $port
        $result.listeningPorts = @(Get-ProcessListeningPorts -ServerProcessId $process.Id)
        $baseUri = "http://127.0.0.1:$port"
        Write-Note ("$Label : the process bound $($result.listeningPorts -join ', '); talking to $baseUri")

        $root = Wait-ForReady -BaseUri "$baseUri/" -TimeoutSeconds $TimeoutSeconds -Process $process
        if (-not $root) {
            Show-ServerOutput $StateRoot
            Deny 'readiness' ("$Label : '/' did not answer with a non-503 status that survived " +
                "${LIVENESS_SECONDS}s of liveness within ${TimeoutSeconds}s")
        }
        $result.rootStatus = $root.status
        $result.rootLocation = $root.location
        Write-Note ("$Label : '/' answered $($root.status) (Location: '$($root.location)') and the " +
            "process was still running ${LIVENESS_SECONDS}s later")

        $entry = Test-EntryDocument -BaseUri $baseUri
        $result.entryDocumentStatus = $entry.status
        if (-not $entry.ok) {
            Show-ServerOutput $StateRoot
            Deny 'web-bootstrap' ("$Label : $($entry.reason) at '$($entry.uri)'")
        }
        $result.entryDocumentReference = [string]$entry.reference
        Write-Note ("$Label : '$($entry.uri)' answered 200 and references the hashed bundle")

        $result.started = $true
        return $result
    } finally {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
        try { $null = $process.WaitForExit(120000) } catch { }
    }
}

# ===========================================================================
# The oracle. Assembles nothing, starts nothing, writes nothing.
# ===========================================================================

function Invoke-Oracle {
    switch ($Oracle) {
        'readiness' {
            if (-not $BaseUrl) { Deny 'oracle' 'the readiness oracle needs -BaseUrl' }
            $answer = Wait-ForReady -BaseUri $BaseUrl -TimeoutSeconds $OracleTimeoutSeconds `
                -ProbeProcessId $OracleProcessId
            if ($answer) {
                Write-Host ("W2-A3 ORACLE readiness: ready status=$($answer.status)")
                return 0
            }
            Write-Host 'W2-A3 ORACLE readiness: not-ready'
            return 1
        }
        'port' {
            $ports = @()
            if ($OracleListeningPorts) {
                $ports = @($OracleListeningPorts -split ',' |
                    Where-Object { $_.Trim() } | ForEach-Object { [int]$_.Trim() })
            }
            $port = Resolve-ServerPort -UsePortsOverride -PortsOverride $ports `
                -TimeoutSeconds $OracleTimeoutSeconds
            Write-Host ("W2-A3 ORACLE port: $port")
            return 0
        }
        'bundle' {
            if (-not $BaseUrl) { Deny 'oracle' 'the bundle oracle needs -BaseUrl' }
            $entry = Test-EntryDocument -BaseUri $BaseUrl
            if ($entry.ok) {
                Write-Host ("W2-A3 ORACLE bundle: present $($entry.reference)")
                return 0
            }
            Write-Host ("W2-A3 ORACLE bundle: absent -- $($entry.reason)")
            return 1
        }
    }
    Deny 'oracle' "unknown oracle '$Oracle'"
}

# ===========================================================================
# The proof
# ===========================================================================

try {
    if ($PSCmdlet.ParameterSetName -eq 'Oracle') {
        $oracleCode = @(Invoke-Oracle) | Select-Object -Last 1
        exit ([int]$oracleCode)
    }

    if ($SourceDateEpoch -le 0) {
        Deny 'source-date-epoch' ('no SOURCE_DATE_EPOCH was given. The frozen assembler requires ' +
            'it and never defaults it to the clock, and this proof does not default it either.')
    }

    $repo = [System.IO.Path]::GetFullPath($RepoRoot)
    if (-not [System.IO.Directory]::Exists($repo)) { Deny 'prerequisite' "no repository at '$repo'" }
    $work = [System.IO.Path]::GetFullPath($WorkDir)
    if ([System.IO.Directory]::Exists($work) -and
        @([System.IO.Directory]::EnumerateFileSystemEntries($work)).Count -gt 0) {
        Deny 'work-dir' ("'$work' is not empty. A relocation proof that inherited an earlier " +
            'extracted tree would prove nothing about relocation.')
    }
    $null = [System.IO.Directory]::CreateDirectory($work)

    $assembler = [System.IO.Path]::Combine($repo, 'ci', 'windows', 'w2', 'assemble-server-zip.ps1')
    if (-not [System.IO.File]::Exists($assembler)) {
        Deny 'prerequisite' ("the frozen W2-A2 assembler is missing at '$assembler'")
    }
    if (-not [System.IO.File]::Exists($OrasPath)) {
        Deny 'prerequisite' ("no ORAS client at '$OrasPath'")
    }

    # ── 1. the archive, from the FROZEN assembler, in this job ───────────────
    #
    # Not downloaded, not taken from a previous run and not taken from an
    # Actions artifact: W0 §8.7 forbids that handover in production, and an
    # archive that came from elsewhere is not the archive this commit builds.
    $assemblyWork = [System.IO.Path]::Combine($work, 'assembly', 'work')
    $assemblyOut = [System.IO.Path]::Combine($work, 'assembly', 'out')
    $assemblerArguments = @{
        RepoRoot = $repo
        WorkDir = $assemblyWork
        OutDir = $assemblyOut
        SourceDateEpoch = $SourceDateEpoch
        OrasPath = $OrasPath
    }
    if ($PythonPath) { $assemblerArguments['PythonPath'] = $PythonPath }
    & $assembler @assemblerArguments
    if ($LASTEXITCODE -ne 0) { Deny 'assembly' 'the frozen W2-A2 assembler produced no archive' }

    $archives = @(Get-ChildItem -LiteralPath $assemblyOut -Filter '*.zip' -File)
    if ($archives.Count -ne 1) {
        Deny 'assembly' ("the frozen assembler wrote $($archives.Count) archives; exactly one is " +
            'the package under test')
    }
    $archive = $archives[0]
    $archiveDigest = Get-Sha256 $archive.FullName
    Write-Note ("assembled $($archive.Name), $($archive.Length) bytes, sha256 $archiveDigest")

    # ── 2. extracted ONCE, into a hostile directory name ─────────────────────
    $hostileRoot = [System.IO.Path]::Combine($work, (Get-HostileLeaf))
    $null = [System.IO.Directory]::CreateDirectory($hostileRoot)
    $extractions = 0
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive.FullName, $hostileRoot)
    $extractions++

    $tops = @(Get-ChildItem -LiteralPath $hostileRoot -Force)
    if ($tops.Count -ne 1 -or -not $tops[0].PSIsContainer) {
        Deny 'top-level' ("the archive extracted $($tops.Count) top-level entries into " +
            "'$hostileRoot'; W0 §6 requires one directory so extraction never scatters files " +
            'into the current directory')
    }
    $packageName = $tops[0].Name
    $firstRoot = $tops[0].FullName
    Write-Note ("extracted once into '$hostileRoot' under one top-level directory '$packageName'")

    # The relocated copy is the thing under test. A start from the assembler's
    # publish tree or from its stage would prove that the SERVER runs, which W0
    # already measured, and nothing at all about the PACKAGE being portable.
    $forbiddenRoots = @(
        [System.IO.Path]::GetFullPath($assemblyWork),
        [System.IO.Path]::GetFullPath($assemblyOut)
    )
    function Assert-NotBuildTree {
        param([string] $Path, [string] $Label)
        $full = [System.IO.Path]::GetFullPath($Path)
        foreach ($root in $forbiddenRoots) {
            $rooted = $root
            if (-not $rooted.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
                $rooted += [System.IO.Path]::DirectorySeparatorChar
            }
            if ($full.StartsWith($rooted, [System.StringComparison]::OrdinalIgnoreCase)) {
                Deny 'build-tree' ("$Label would start from '$full', which is inside the " +
                    "assembler's own tree at '$root'. The relocated package is what is under " +
                    'test; the build tree is not.')
            }
        }
    }

    Assert-NotBuildTree $firstRoot 'the first start'
    $firstExeDigest = Get-Sha256 ([System.IO.Path]::Combine($firstRoot, 'tesserafin.exe'))

    # ── 3. the first start, from the hostile path ────────────────────────────
    $firstState = [System.IO.Path]::Combine($work, 'state-1')
    $first = Invoke-ServerStart -Label 'start-1 (hostile path)' -PackageRoot $firstRoot `
        -StateRoot $firstState -TimeoutSeconds $ReadyTimeoutSeconds

    # ── 4. the SAME tree, moved to a different depth ─────────────────────────
    #
    # Moved, not extracted again. The archive is opened exactly once above and
    # $extractions says so; the exe is hashed on both sides of the move and the
    # first location is required to be gone afterwards, because "the same tree"
    # is a claim that has to be measurable rather than asserted.
    $relocatedParent = [System.IO.Path]::Combine($work, 'reloc', 'deeper')
    $null = [System.IO.Directory]::CreateDirectory($relocatedParent)
    $secondRoot = [System.IO.Path]::Combine($relocatedParent, $packageName)

    $firstDepth = @($firstRoot.Split([System.IO.Path]::DirectorySeparatorChar)).Count
    $secondDepth = @($secondRoot.Split([System.IO.Path]::DirectorySeparatorChar)).Count
    if ($firstDepth -eq $secondDepth) {
        Deny 'relocation' ("the second location '$secondRoot' sits at the same depth as the " +
            "first '$firstRoot'; §2.3 asks for a different one")
    }

    # A bounded retry, because a virus scanner or an indexer can still hold a
    # handle for a moment after the process exits. One transient lock is a
    # wasted hosted run, not a finding about relocation.
    $moved = $false
    $moveError = ''
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try { Move-Item -LiteralPath $firstRoot -Destination $secondRoot -Force; $moved = $true; break }
        catch { $moveError = $_.Exception.Message; Start-Sleep -Milliseconds 1500 }
    }
    if (-not $moved) {
        Deny 'relocation' ("the tree could not be moved from '$firstRoot' to '$secondRoot': $moveError")
    }
    if ([System.IO.Directory]::Exists($firstRoot)) {
        Deny 'relocation' ("'$firstRoot' still exists after the move, so the second start would " +
            'not be running the same tree')
    }
    if ($extractions -ne 1) {
        Deny 'relocation' ("the archive was extracted $extractions times; the second start must " +
            'be the SAME tree moved, not a second extract')
    }
    Assert-NotBuildTree $secondRoot 'the second start'

    $secondExeDigest = Get-Sha256 ([System.IO.Path]::Combine($secondRoot, 'tesserafin.exe'))
    if ($secondExeDigest -cne $firstExeDigest) {
        Deny 'relocation' ("tesserafin.exe hashes to $secondExeDigest after the move and " +
            "$firstExeDigest before it; that is not the same tree")
    }
    Write-Note ("moved the SAME tree from depth $firstDepth to depth $secondDepth at '$secondRoot'")

    # ── 5. the second start, with state that shares nothing with the first ───
    $secondState = [System.IO.Path]::Combine($work, 'state-2')
    if ([System.IO.Path]::GetFullPath($secondState) -eq [System.IO.Path]::GetFullPath($firstState)) {
        Deny 'state' 'both starts were given the same state root'
    }
    $second = Invoke-ServerStart -Label 'start-2 (relocated, deeper)' -PackageRoot $secondRoot `
        -StateRoot $secondState -TimeoutSeconds $ReadyTimeoutSeconds

    # ── 6. the properties that are only true of the PAIR ─────────────────────
    $sharedState = @($first.stateDirectories | Where-Object { $second.stateDirectories -contains $_ })
    if ($sharedState.Count -gt 0) {
        Deny 'state' ("the two starts shared $($sharedState -join ', '); a proof that reuses " +
            'state cannot tell a fresh install from an upgrade, and W0 §2.3 needs both answers')
    }
    if ($first.discoveredPort -eq $second.discoveredPort) {
        Deny 'port' ("both starts reported port $($first.discoveredPort). Two starts that were " +
            'each asked for a different free port cannot both bind the same one, so an equal ' +
            'pair means the port was not read from the process at all')
    }
    foreach ($run in @($first, $second)) {
        if ($run.listeningPorts -notcontains $run.discoveredPort) {
            Deny 'port' ("$($run.label) talked to port $($run.discoveredPort), which is not one " +
                "the process bound ($($run.listeningPorts -join ', '))")
        }
        if ($run.processPath -and
            ([System.IO.Path]::GetFullPath($run.processPath) -ne [System.IO.Path]::GetFullPath($run.exe))) {
            Deny 'relocated-tree' ("$($run.label) launched '$($run.exe)' but the running image " +
                "was '$($run.processPath)'")
        }
    }

    # ── 7. the evidence, written only now ────────────────────────────────────
    $evidence = [ordered]@{
        schemaVersion = 1
        manifest = 'tesserafin-windows-server-zip-relocate-start'
        slice = 'W2-A3'
        tracker = 256
        archiveName = $archive.Name
        archiveSha256 = $archiveDigest
        archiveBytes = $archive.Length
        topLevelDirectory = $packageName
        sourceDateEpoch = $SourceDateEpoch
        extractions = $extractions
        exeSha256 = $firstExeDigest
        relocation = [ordered]@{
            firstDepth = $firstDepth
            secondDepth = $secondDepth
            sameTree = ($firstExeDigest -ceq $secondExeDigest)
            reExtracted = ($extractions -gt 1)
        }
        starts = @(
            [ordered]@{
                label = $first.label
                discoveredPort = $first.discoveredPort
                requestedPort = $first.requestedPort
                listeningPorts = $first.listeningPorts
                rootStatus = $first.rootStatus
                rootLocation = $first.rootLocation
                livenessSeconds = $first.livenessSeconds
                entryDocumentStatus = $first.entryDocumentStatus
                entryDocumentReference = $first.entryDocumentReference
            },
            [ordered]@{
                label = $second.label
                discoveredPort = $second.discoveredPort
                requestedPort = $second.requestedPort
                listeningPorts = $second.listeningPorts
                rootStatus = $second.rootStatus
                rootLocation = $second.rootLocation
                livenessSeconds = $second.livenessSeconds
                entryDocumentStatus = $second.entryDocumentStatus
                entryDocumentReference = $second.entryDocumentReference
            }
        )
        portSource = 'the listening TCP ports of the server process'
        readinessRule = "'/' answers a status other than 503 and the process is still running 3s later"
        webBootstrapRule = 'the entry document references the hashed main.tesserafin bundle'
        serviceRegistered = $false
        published = $false
        actionsArtifactHandoff = $false
    }
    $evidenceJson = ($evidence | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
    $evidenceDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($EvidencePath))
    if ($evidenceDirectory) { $null = [System.IO.Directory]::CreateDirectory($evidenceDirectory) }
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($EvidencePath), $evidenceJson + "`n",
        (New-Object System.Text.UTF8Encoding($false)))

    Write-Note ("start-1 port $($first.discoveredPort), '/' $($first.rootStatus); " +
        "start-2 port $($second.discoveredPort), '/' $($second.rootStatus)")
    Write-Note ("evidence at $EvidencePath")
    exit 0
} catch {
    $message = $_.Exception.Message
    if (-not $message.StartsWith('W2-A3 DENY')) { $message = "W2-A3 DENY [unexpected] $message" }
    [Console]::Error.WriteLine($message)
    exit 1
}
