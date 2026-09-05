#Requires -Version 7.2
<#
.SYNOPSIS
    Register, start, stop and remove the Tesserafin Windows service from an
    extracted portable ZIP -- and refuse, rather than pretend, whenever the
    thing being asked for is something a script is not allowed to be.

.DESCRIPTION
    W2-A5 (#256). `docs/distribution/W0-windows-server.md` §6 requires the
    portable ZIP to carry

        "a first-party PowerShell script that registers, starts, stops and
        removes the service for operators who prefer the ZIP. That script is a
        convenience over the same contract as §4 -- it is **not** a second
        installer and gets no repair, rollback or Add/Remove Programs entry,
        which are properties of the format that no script can add."

    This file is that script, and the sentence above is its whole specification.
    It ships INSIDE the archive, at the top level of
    `tesserafin-server_<version>_win-x64/`, beside `tesserafin.exe`. Its own
    location is the package: every path it hands the Service Control Manager is
    derived from `$PSScriptRoot`, so moving the extracted tree and registering
    again needs no edit to this file and no argument describing where the tree
    went.

    ── What it is a convenience OVER ─────────────────────────────────────────

    §4 selects "direct .NET Generic Host Windows Service integration in
    `Tesserafin.Server`" and tabulates the service contract: the name
    `Tesserafin`, the display name `Tesserafin Server`, the description, startup
    mode `Automatic (Delayed Start)`, the recovery policy, and an argument list
    that always passes `--webdir` and `--ffmpeg` explicitly "so the service can
    never silently fall back to a `PATH` encoder or to a stale web directory".
    That table is reproduced here as constants, not paraphrased, and `register`
    hands exactly it to the SCM.

    ── What it deliberately is NOT ───────────────────────────────────────────

      * NO REPAIR. `register` refuses a service that already exists. Rewriting
        the configuration of a registered service is repair by another name,
        and it is the operation that silently turns a broken registration into
        a differently broken one. `remove` then `register` is the supported
        sequence, and it is two decisions by the operator rather than one
        guess by a script.

      * NO ROLLBACK. If `register` fails after the SCM has already accepted
        `sc.exe create`, this script says exactly what exists and stops. It
        does not delete the service it just made. §6 calls rollback a property
        of the format "that no script can add", and a script that half-performs
        one leaves the operator with neither the service nor the certainty that
        there is no service.

      * NO ADD/REMOVE PROGRAMS ENTRY. Nothing here writes
        `HKLM\...\CurrentVersion\Uninstall`, and nothing here runs an installer
        engine. A ZIP that advertised itself in Programs and Features would be
        claiming an uninstall contract it cannot honour.

      * NO SECOND COPY OF THE SERVER. The service runs the `tesserafin.exe`
        that is already in this directory. Nothing is copied to
        `%ProgramFiles%`, and there is therefore no second tree that can drift
        from the one the operator extracted.

      * NO BAKED INSTALL LOCATION. There is no constant in this file naming a
        directory on the machine. `$PSScriptRoot` is the only source of the
        package path.

      * NO STATE. §6: the ZIP "ships **no** state. Configuration, database,
        cache and logs are always given by argument." `register` therefore
        REQUIRES `-DataDir`, `-ConfigDir`, `-CacheDir` and `-LogDir`, refuses
        any of them that is relative, and refuses any of them that resolves
        inside the package directory -- which would put operator state inside
        the tree the next upgrade replaces wholesale.

      * NO MACHINE-WIDE SETTINGS. §4's 120 s stop timeout is
        `HKLM\SYSTEM\CurrentControlSet\Control\ServicesPipeTimeout`, which is
        machine-wide and affects every service on the host, and §4's
        `NT SERVICE\Tesserafin` identity is only usable once §9's ACL grants
        exist on the state directories. Both are installer acts. This script
        performs neither and says so out loud instead of registering an
        identity that could not reach its own data directory. W3's MSI owns
        them.

    ── The service does not start at this master, and that is not hidden ─────

    §4 measured it: the unmodified `tesserafin.exe`, registered with the SCM and
    started, "fails with error 1053 after 7 seconds" because a plain console
    executable never calls `StartServiceCtrlDispatcher`. §4 names that "a
    missing boundary in the **server**" that "no installer technology can paper
    over", and closing it is W3's work. So `start` reports the SCM's own verdict
    verbatim rather than polling until something looks alive: a script that
    dressed 1053 up as a slow start would be hiding the one fact W3 exists to
    fix.

.PARAMETER Verb
    One of `register`, `start`, `stop`, `remove`.

    Deliberately NOT a `[ValidateSet]`. A rejected set member produces
    PowerShell's own binding error, and "that is not one of the four verbs" is
    something this script should say in its own words, next to the list.

.PARAMETER DataDir
.PARAMETER ConfigDir
.PARAMETER CacheDir
.PARAMETER LogDir
    `register` only, and all four are required there. They are the operator's
    directories: §6 gives configuration, database, cache and logs by argument,
    and nothing in the archive supplies a default for any of them. They are
    created if absent, because the SCM does not create them and a service that
    cannot reach its data directory fails in a way that reads like a packaging
    fault. `remove` never deletes them.

.PARAMETER Plan
    CONTROLS ONLY. Resolve every path, build the exact SCM command lines, and
    write them to stdout as one JSON document -- without contacting the Service
    Control Manager, without requiring administrator, and without requiring
    Windows.

    It exists because the evidence this slice is allowed to produce is
    unit-level and dry: registering a service needs administrator and an SCM,
    and W2-A5 is explicitly not authorised to start one on a runner. `-Plan`
    lets `ci/windows/w2/service-script-controls.py` drive the REAL argument
    construction, the REAL path resolution and the REAL refusals rather than a
    second copy of them written for a test. It is modelled on the frozen
    assembler's own `-StageRoot` pack-only parameter set and on the W2-A3
    proof's `-Oracle` set, and the controls assert it registers nothing.
#>

[CmdletBinding(DefaultParameterSetName = 'Verb')]
param(
    [Parameter(Mandatory = $true, Position = 0, ParameterSetName = 'Verb')]
    [Parameter(Mandatory = $true, Position = 0, ParameterSetName = 'Plan')]
    [string] $Verb,

    [Parameter(ParameterSetName = 'Verb')]
    [Parameter(ParameterSetName = 'Plan')]
    [string] $DataDir,

    [Parameter(ParameterSetName = 'Verb')]
    [Parameter(ParameterSetName = 'Plan')]
    [string] $ConfigDir,

    [Parameter(ParameterSetName = 'Verb')]
    [Parameter(ParameterSetName = 'Plan')]
    [string] $CacheDir,

    [Parameter(ParameterSetName = 'Verb')]
    [Parameter(ParameterSetName = 'Plan')]
    [string] $LogDir,

    [Parameter(Mandatory = $true, ParameterSetName = 'Plan')]
    [switch] $Plan
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# PowerShell 7.3 changed how native command arguments are quoted. `Standard` is
# the mode in which an element containing spaces arrives at sc.exe as ONE
# argument, which is the whole of what `binPath=` needs. Setting it explicitly
# means this script does not depend on the host's preference variable.
$PSNativeCommandArgumentPassing = 'Standard'

# ---------------------------------------------------------------------------
# W0 §4, "The service contract", transcribed. These are the identity the MSI
# will register too; the ZIP and the MSI must not disagree about what a
# Tesserafin service IS.
# ---------------------------------------------------------------------------
$SERVICE_NAME = 'Tesserafin'
$SERVICE_DISPLAY_NAME = 'Tesserafin Server'
$SERVICE_DESCRIPTION = 'Tesserafin media server. Manage it at http://localhost:8096.'
$SERVICE_START_TYPE = 'delayed-auto'

# §4: "restart after 60 s on first and second failure; no action on the third,
# so a crash loop is visible rather than hidden". `reset=` is the window after
# which the SCM forgets earlier failures.
$FAILURE_RESET_SECONDS = 86400
$FAILURE_ACTIONS = 'restart/60000/restart/60000//0'

# ---------------------------------------------------------------------------
# The relative layout the frozen W2-A2 assembler stages, mirroring W0 §9.1, and
# the same two constants the W2-A3 relocate-and-start proof reads. They are
# resolved against $PSScriptRoot and against nothing else.
# ---------------------------------------------------------------------------
$SERVER_RELATIVE_EXE = 'tesserafin.exe'
$WEB_RELATIVE_DIR = 'web'
$FFMPEG_RELATIVE_EXE = 'ffmpeg/bin/ffmpeg.exe'

$VERBS = @('register', 'start', 'stop', 'remove')

# How long to wait for the SCM to reach a steady state after `start` or `stop`.
# Not §4's 120 s stop timeout: that is the SCM's own machine-wide setting, which
# this script does not write. This is only how long the script watches before
# reporting what the service's state actually is.
$STATE_WAIT_SECONDS = 150

# ---------------------------------------------------------------------------

function Deny {
    param([string] $Category, [string] $Message)
    throw ("W2-A5 DENY [{0}] {1}" -f $Category, $Message)
}

function Write-Note {
    param([string] $Message)
    [Console]::Error.WriteLine("W2-A5 $Message")
}

function Assert-Windows {
    # Fail closed rather than pretend. On Linux and macOS there is no Service
    # Control Manager at all, and every verb below would otherwise fail deep
    # inside a missing sc.exe with a message about a command not being found.
    if (-not $IsWindows) {
        Deny 'platform' ('the Windows Service Control Manager exists only on Windows; this host ' +
            "reports $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription.Trim())")
    }
}

function Assert-Administrator {
    # An unelevated sc.exe returns "Access is denied" (error 5) from a call the
    # operator believes registered a service. Refusing here means the failure is
    # named once, before anything has been attempted, instead of appearing as an
    # opaque exit code from a helper.
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Deny 'privilege' ("'$Verb' changes the Service Control Manager, which requires an " +
            "elevated session. Re-run this script from an administrator PowerShell. Nothing " +
            'has been changed.')
    }
}

function Get-PackageRoot {
    # The package IS this script's directory. There is no parameter for it and
    # no constant naming one: an install location that can be overridden is an
    # install location that can point at a tree the operator did not extract,
    # and a baked one is the thing §6 forbids.
    if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        Deny 'package' 'this script must be run from a file, so that its own directory is the package'
    }
    return [System.IO.Path]::GetFullPath($PSScriptRoot)
}

function Resolve-PackagePaths {
    param([Parameter(Mandatory = $true)] [string] $PackageRoot)

    $separator = [System.IO.Path]::DirectorySeparatorChar
    $paths = [ordered]@{
        packageRoot = $PackageRoot
        serverExe = [System.IO.Path]::Combine($PackageRoot, $SERVER_RELATIVE_EXE)
        webDir = [System.IO.Path]::Combine($PackageRoot, $WEB_RELATIVE_DIR)
        ffmpegExe = [System.IO.Path]::Combine($PackageRoot,
            ($FFMPEG_RELATIVE_EXE -replace '/', $separator))
    }
    if (-not [System.IO.File]::Exists($paths.serverExe)) {
        Deny 'package' ("this directory carries no '$SERVER_RELATIVE_EXE'. Run this script from " +
            'inside the extracted portable ZIP, not from a copy of the script on its own.')
    }
    if (-not [System.IO.Directory]::Exists($paths.webDir)) {
        Deny 'package' ("this directory carries no '$WEB_RELATIVE_DIR'; the bundled Web payload " +
            'is part of the package and --webdir is always passed explicitly')
    }
    if (-not [System.IO.File]::Exists($paths.ffmpegExe)) {
        Deny 'package' ("this directory carries no '$FFMPEG_RELATIVE_EXE'; --ffmpeg is always " +
            'passed explicitly so the service can never fall back to a PATH encoder')
    }
    return $paths
}

function Resolve-StateDirectories {
    param([Parameter(Mandatory = $true)] [string] $PackageRoot)

    # Named, not defaulted. §6: "Ships **no** state. Configuration, database,
    # cache and logs are always given by argument." A default here would be a
    # location this package chose, which is exactly the state the ZIP does not
    # ship. They are validated rather than declared Mandatory so that the
    # refusal is this script's sentence and not PowerShell's prompt.
    $given = [ordered]@{
        '-DataDir' = $DataDir
        '-ConfigDir' = $ConfigDir
        '-CacheDir' = $CacheDir
        '-LogDir' = $LogDir
    }
    $missing = @($given.Keys | Where-Object { [string]::IsNullOrWhiteSpace($given[$_]) })
    if ($missing.Count -gt 0) {
        Deny 'state-directories' ("$($missing -join ', ') " +
            "$(if ($missing.Count -eq 1) { 'is' } else { 'are' }) required. The portable ZIP " +
            'ships no state and names no default location for it, so configuration, database, ' +
            'cache and logs are always given by argument.')
    }

    $resolved = [ordered]@{}
    foreach ($name in $given.Keys) {
        $value = $given[$name]
        if (-not [System.IO.Path]::IsPathRooted($value)) {
            Deny 'state-directories' ("$name '$value' is relative. The Service Control Manager " +
                'starts a service with a working directory this script does not choose, so a ' +
                'relative state directory would resolve somewhere neither of us picked.')
        }
        $full = [System.IO.Path]::GetFullPath($value)
        $inside = $full.Equals($PackageRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $full.StartsWith($PackageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
                [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
        if ($inside) {
            Deny 'state-directories' ("$name '$full' is inside the package directory. The package " +
                'is replaced wholesale on upgrade (W0 §9.1) and ships no state, so operator ' +
                'state kept inside it would be destroyed by the next extraction.')
        }
        $resolved[$name.TrimStart('-')] = $full
    }
    return $resolved
}

function New-ServiceArguments {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [Parameter(Mandatory = $true)] $State
    )
    # W0 §4, verbatim in shape: --service, the four operator directories, and
    # --webdir and --ffmpeg "always passed explicitly, exactly as the Linux unit
    # does". `--nowebclient` is never used.
    return @(
        '--service'
        '--configdir', $State.ConfigDir
        '--datadir', $State.DataDir
        '--cachedir', $State.CacheDir
        '--logdir', $State.LogDir
        '--webdir', $Paths.webDir
        '--ffmpeg', $Paths.ffmpegExe
    )
}

function Format-BinaryPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Executable,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )
    # The SCM stores ONE string and re-splits it with the ordinary Windows
    # command-line rules, so every element that could contain a space is quoted
    # here. W0 §2.3 records what the unquoted form costs: a path truncated at
    # its first space and a server that died writing its marker into the wrong
    # directory.
    foreach ($piece in @($Executable) + $Arguments) {
        if ($piece.Contains('"')) {
            Deny 'service-arguments' ("'$piece' contains a double quote, which the Service " +
                'Control Manager command line cannot carry unambiguously')
        }
    }
    $parts = @('"' + $Executable + '"')
    foreach ($argument in $Arguments) {
        if ($argument.StartsWith('--')) { $parts += $argument } else { $parts += '"' + $argument + '"' }
    }
    return ($parts -join ' ')
}

function Get-ServiceRecord {
    # `sc.exe query` rather than Get-Service: Get-Service throws a terminating
    # error for an absent service under $ErrorActionPreference = 'Stop', and
    # "there is no such service" is an ANSWER to `register` and to `remove`, not
    # a failure of the script.
    $output = & sc.exe query $SERVICE_NAME 2>&1 | Out-String
    $code = $LASTEXITCODE
    # 1060: ERROR_SERVICE_DOES_NOT_EXIST.
    if ($code -eq 1060) { return $null }
    if ($code -ne 0) {
        Deny 'scm' ("sc.exe query $SERVICE_NAME failed with exit code ${code}: $($output.Trim())")
    }
    $state = 'UNKNOWN'
    $match = [regex]::Match($output, '(?im)^\s*STATE\s*:\s*\d+\s+(\S+)')
    if ($match.Success) { $state = $match.Groups[1].Value }
    return [ordered]@{ name = $SERVICE_NAME; state = $state; query = $output.Trim() }
}

function Invoke-Sc {
    param(
        [Parameter(Mandatory = $true)] [string] $What,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )
    $output = & sc.exe @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Deny 'scm' ("$What failed with exit code ${code}: $($output.Trim())")
    }
    return $output.Trim()
}

function Wait-ServiceState {
    param(
        [Parameter(Mandatory = $true)] [string] $Expected,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $record = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $record = Get-ServiceRecord
        if ($null -eq $record) { return $null }
        if ($record.state -eq $Expected) { return $record }
        Start-Sleep -Milliseconds 500
    }
    return $record
}

function Get-ScInvocations {
    param(
        [Parameter(Mandatory = $true)] [string] $Action,
        [string] $BinaryPath
    )
    # The single definition of what each verb asks the Service Control Manager
    # to do. The verbs execute what this returns and `-Plan` prints what this
    # returns, so the dry evidence is about the production call rather than
    # beside it.
    switch ($Action) {
        'register' {
            return @(
                [ordered]@{ what = "sc.exe create $SERVICE_NAME"; arguments = @(
                    'create', $SERVICE_NAME,
                    'binPath=', $BinaryPath,
                    'start=', $SERVICE_START_TYPE,
                    'DisplayName=', $SERVICE_DISPLAY_NAME) }
                [ordered]@{ what = "sc.exe description $SERVICE_NAME"; arguments = @(
                    'description', $SERVICE_NAME, $SERVICE_DESCRIPTION) }
                [ordered]@{ what = "sc.exe failure $SERVICE_NAME"; arguments = @(
                    'failure', $SERVICE_NAME,
                    'reset=', "$FAILURE_RESET_SECONDS",
                    'actions=', $FAILURE_ACTIONS) }
            )
        }
        'start' {
            return @([ordered]@{ what = "sc.exe start $SERVICE_NAME"
                                 arguments = @('start', $SERVICE_NAME) })
        }
        'stop' {
            return @([ordered]@{ what = "sc.exe stop $SERVICE_NAME"
                                 arguments = @('stop', $SERVICE_NAME) })
        }
        'remove' {
            return @([ordered]@{ what = "sc.exe delete $SERVICE_NAME"
                                 arguments = @('delete', $SERVICE_NAME) })
        }
    }
    Deny 'verb' "no Service Control Manager call is defined for '$Action'"
}


function New-Plan {
    param(
        [Parameter(Mandatory = $true)] [string] $Action,
        [Parameter(Mandatory = $true)] $Paths,
        $State,
        [string[]] $ServiceArguments,
        [string] $BinaryPath
    )
    $invocations = @(Get-ScInvocations -Action $Action -BinaryPath $BinaryPath |
        ForEach-Object { [ordered]@{ what = $_.what; arguments = @($_.arguments) } })

    $plan = [ordered]@{
        schemaVersion = 1
        document = 'tesserafin-windows-service-plan'
        slice = 'W2-A5'
        contract = 'docs/distribution/W0-windows-server.md §4, carried by §6'
        action = $Action
        serviceName = $SERVICE_NAME
        displayName = $SERVICE_DISPLAY_NAME
        description = $SERVICE_DESCRIPTION
        startType = $SERVICE_START_TYPE
        failureResetSeconds = $FAILURE_RESET_SECONDS
        failureActions = $FAILURE_ACTIONS
        packageRoot = $Paths.packageRoot
        serverExecutable = $Paths.serverExe
        webDirectory = $Paths.webDir
        ffmpegExecutable = $Paths.ffmpegExe
        serviceArguments = @($ServiceArguments)
        binaryPath = $BinaryPath
        stateDirectories = $State
        # The EXACT argv this script would hand sc.exe, element by element, and
        # not a rendering of it: a single joined string would have to re-invent
        # the quoting rules, and a plan whose quoting differs from the call it
        # describes is a plan that cannot be used as evidence about the call.
        # Both this document and the verbs below read it from the same function.
        scInvocations = @($invocations)
        isInstaller = $false
        performsRepair = $false
        performsRollback = $false
        writesAddRemoveProgramsEntry = $false
        copiesTheServer = $false
        bakesAnInstallLocation = $false
        writesMachineWideSettings = $false
        shipsState = $false
        # Named so that "not done" is a recorded decision rather than an
        # omission a reader has to notice. Both belong to W3's MSI.
        deferredToW3 = @(
            'ServicesPipeTimeout (W0 §4 stop timeout) is machine-wide and is not written here',
            'the NT SERVICE\Tesserafin identity needs the W0 §9 ACL grants and is not registered here'
        )
        knownAtThisMaster = ('W0 §4 measured the unmodified tesserafin.exe failing SCM start with ' +
            'error 1053 after 7 seconds; the --service boundary is W3 work and this script reports ' +
            'the SCM verdict rather than masking it')
    }
    return (($plan | ConvertTo-Json -Depth 6) -replace "`r`n", "`n")
}

# ===========================================================================

try {
    $action = $Verb.Trim().ToLowerInvariant()
    if ($VERBS -notcontains $action) {
        Deny 'verb' ("'$Verb' is not one of the four verbs this script has: " +
            ($VERBS -join ', ') + '. It registers, starts, stops and removes a service; it ' +
            'installs, repairs, upgrades and uninstalls nothing.')
    }

    # `-Plan` resolves and reports. It reaches no Service Control Manager, so it
    # deliberately does not run the platform and privilege gates: requiring
    # administrator to PRINT a command line would make the dry evidence
    # unobtainable on the very hosts it exists for.
    if (-not $Plan) {
        Assert-Windows
        Assert-Administrator
    }

    $packageRoot = Get-PackageRoot
    $paths = Resolve-PackagePaths -PackageRoot $packageRoot

    $state = $null
    $serviceArguments = @()
    $binaryPath = ''
    if ($action -eq 'register') {
        $state = Resolve-StateDirectories -PackageRoot $packageRoot
        $serviceArguments = New-ServiceArguments -Paths $paths -State $state
        $binaryPath = Format-BinaryPath -Executable $paths.serverExe -Arguments $serviceArguments
    }

    if ($Plan) {
        [Console]::Out.WriteLine((New-Plan -Action $action -Paths $paths -State $state `
            -ServiceArguments $serviceArguments -BinaryPath $binaryPath))
        exit 0
    }

    $existing = Get-ServiceRecord

    switch ($action) {
        'register' {
            if ($null -ne $existing) {
                Deny 'register' ("a service named '$SERVICE_NAME' is already registered, in state " +
                    "$($existing.state). This script does not reconfigure an existing service: " +
                    'rewriting a registration in place is repair, and §6 gives this script no ' +
                    "repair. Run '$SERVER_RELATIVE_EXE' service removal first: " +
                    './tesserafin-server-service.ps1 remove')
            }
            foreach ($directory in $state.Values) {
                $null = [System.IO.Directory]::CreateDirectory($directory)
            }
            # From here on a failure leaves the SCM changed and this script does
            # NOT undo it. §6 gives the format no rollback, and a script that
            # deleted a service it had just created would be inventing one badly.
            $invocations = @(Get-ScInvocations -Action 'register' -BinaryPath $binaryPath)
            $null = Invoke-Sc -What $invocations[0].what -Arguments $invocations[0].arguments
            Write-Note "registered '$SERVICE_NAME' ($SERVICE_DISPLAY_NAME), start=$SERVICE_START_TYPE"
            try {
                foreach ($invocation in $invocations[1..($invocations.Count - 1)]) {
                    $null = Invoke-Sc -What $invocation.what -Arguments $invocation.arguments
                }
            } catch {
                Deny 'register' ("the service '$SERVICE_NAME' WAS created and then " +
                    "$($_.Exception.Message). Nothing has been rolled back, because this script " +
                    "has no rollback. Run './tesserafin-server-service.ps1 remove' and register " +
                    'again.')
            }
            Write-Note "recovery: restart after 60 s on the first and second failure, no action on the third"
            Write-Note ("state directories are the operator's and were not placed inside the package: " +
                ($state.Values -join ', '))
            Write-Note ("registered. W0 §4 measured this server failing SCM start with error 1053 at " +
                'this revision; the --service boundary is W3 work.')
        }
        'start' {
            if ($null -eq $existing) {
                Deny 'start' ("no service named '$SERVICE_NAME' is registered; run " +
                    "'./tesserafin-server-service.ps1 register' first")
            }
            if ($existing.state -eq 'RUNNING') {
                Write-Note "'$SERVICE_NAME' is already RUNNING"
                exit 0
            }
            # The SCM's verdict is reported, never smoothed over: W0 §4 recorded
            # error 1053 here, and that is the fact W3 closes.
            $invocation = @(Get-ScInvocations -Action 'start')[0]
            $null = Invoke-Sc -What $invocation.what -Arguments $invocation.arguments
            $record = Wait-ServiceState -Expected 'RUNNING' -TimeoutSeconds $STATE_WAIT_SECONDS
            if ($null -eq $record -or $record.state -ne 'RUNNING') {
                $reported = if ($null -eq $record) { 'gone' } else { $record.state }
                Deny 'start' ("'$SERVICE_NAME' did not reach RUNNING within $STATE_WAIT_SECONDS s; " +
                    "the Service Control Manager reports $reported. Check the Windows Event Log " +
                    'and the service log directory.')
            }
            Write-Note "'$SERVICE_NAME' is RUNNING"
        }
        'stop' {
            if ($null -eq $existing) {
                Deny 'stop' ("no service named '$SERVICE_NAME' is registered")
            }
            if ($existing.state -eq 'STOPPED') {
                Write-Note "'$SERVICE_NAME' is already STOPPED"
                exit 0
            }
            $invocation = @(Get-ScInvocations -Action 'stop')[0]
            $null = Invoke-Sc -What $invocation.what -Arguments $invocation.arguments
            $record = Wait-ServiceState -Expected 'STOPPED' -TimeoutSeconds $STATE_WAIT_SECONDS
            if ($null -eq $record -or $record.state -ne 'STOPPED') {
                $reported = if ($null -eq $record) { 'gone' } else { $record.state }
                Deny 'stop' ("'$SERVICE_NAME' did not reach STOPPED within $STATE_WAIT_SECONDS s; " +
                    "the Service Control Manager reports $reported")
            }
            Write-Note "'$SERVICE_NAME' is STOPPED"
        }
        'remove' {
            if ($null -eq $existing) {
                Deny 'remove' ("no service named '$SERVICE_NAME' is registered, so there is " +
                    'nothing to remove')
            }
            if ($existing.state -ne 'STOPPED') {
                # W0 §4: an orphaned process is worse than a clean failure,
                # "because an installer would then be uninstalling a service
                # whose process still holds the database".
                Deny 'remove' ("'$SERVICE_NAME' is $($existing.state). Removing a registration " +
                    'whose process is still running would leave a server holding the database ' +
                    "with no service to stop it. Run './tesserafin-server-service.ps1 stop' first.")
            }
            $invocation = @(Get-ScInvocations -Action 'remove')[0]
            $null = Invoke-Sc -What $invocation.what -Arguments $invocation.arguments
            Write-Note ("removed the '$SERVICE_NAME' registration. No file was deleted: the " +
                'extracted package and the operator directories for configuration, database, cache ' +
                'and logs are untouched, because this script has no uninstall contract and ' +
                'deletes no data it did not create.')
        }
    }
    exit 0
} catch {
    $message = $_.Exception.Message
    if (-not $message.StartsWith('W2-A5 DENY')) { $message = "W2-A5 DENY [unexpected] $message" }
    [Console]::Error.WriteLine($message)
    exit 1
}
