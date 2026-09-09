#Requires -Version 7.2
<#
    W4-A0 (#234). The predicates the MSI skeleton is graded on, and the RED set
    each hostile control is expected to produce.

    Everything here is PURE: an observation hashtable in, an ordered
    predicate-name -> boolean map out. No msiexec, no registry, no filesystem.
    That is deliberate, and it is what makes the grading harness provably not
    inert: `ci/windows/w4/msi-controls.py` drives these same functions on Linux
    with synthetic observations and asserts that each control's declared RED set
    is exactly the set that goes red, BEFORE the hosted job spends two hours
    measuring the real thing. A harness whose controls all fail the same way, or
    all pass, is a harness that grades nothing -- and that failure mode is
    invisible from a green run.
#>

Set-StrictMode -Version 3.0

# ---------------------------------------------------------------------------
# W0 §4, the service contract, restated ONCE for the grader. These two strings
# are the only part of §4 that cannot be read out of an accepted script: the
# W2-A5 script registers the service under the ZIP's own display name, and the
# MSI is the first artifact to carry the contract's own.
# ---------------------------------------------------------------------------
$script:ContractDisplayName = 'Tesserafin Server'
$script:ContractDescription = 'Tesserafin media server. Manage it at http://localhost:8096.'

# W0 §4: `Automatic (Delayed Start)`. SERVICE_AUTO_START is 2 in the SCM's own
# encoding, and the delayed half is a separate REG_DWORD the SCM writes beside
# it -- which is the whole reason this is read back rather than asserted from
# the authoring. WiX answers WIX1149 for the core ServiceConfig element, which
# is the element that still carries this row; the FAILURE-ACTIONS row moved to
# `util:ServiceConfig` under W4-A2-R1 (#234) and is read back below.
$script:ServiceAutoStart = 2

# ---------------------------------------------------------------------------
# W0 §4, the recovery row, restated ONCE for the grader (W4-A2):
#
#     restart after 60 s on first and second failure; no action on the third,
#     so a crash loop is visible rather than hidden
#
# Like the display name and the description above, this is a part of §4 that
# cannot be read out of an accepted script -- the W2-A5 portable-ZIP script
# registers a service and sets no failure policy at all, so the MSI is the
# first artifact to carry this row. `ci/windows/w4/msi-controls.py` states the
# same three actions against the AUTHORING; this file states them against the
# SERVICE the SCM actually ended up with, and the two are only ever both green
# when the authoring and the installed service agree.
#
# The delay is milliseconds, which is the SCM's own unit in SC_ACTION.Delay --
# `sc.exe failure ... actions= restart/60000/restart/60000//0`. The reset
# period is seconds. Both are stated here in the SCM's units and NOT in the
# authoring's: since W4-A2-R1 (#234) the authoring says `60` seconds and `1`
# day to `util:ServiceConfig`, and the extension's custom action does the
# multiplication. This file grades what the SCM ended up with, so it is exactly
# the place that must not restate the authoring's units.
# ---------------------------------------------------------------------------
$script:ContractResetPeriodSeconds = 86400
$script:ContractFailureActions = @(
    @{ type = 'restartService'; delayMs = 60000 }
    @{ type = 'restartService'; delayMs = 60000 }
    @{ type = 'none'; delayMs = 0 }
)

function Split-W4CommandLine {
    <#
        Split a Windows command line into tokens, honouring double quotes. The
        service ImagePath is one string in the registry and every argument
        predicate is a statement about its tokens, so a substring match would
        pass on `--serviceless` and on a `--webdir` that names the wrong tree.
    #>
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $CommandLine)

    $tokens = [System.Collections.Generic.List[string]]::new()
    $current = [System.Text.StringBuilder]::new()
    $inQuotes = $false
    $started = $false

    foreach ($ch in $CommandLine.ToCharArray()) {
        if ($ch -eq '"') { $inQuotes = -not $inQuotes; $started = $true; continue }
        if (-not $inQuotes -and ($ch -eq ' ' -or $ch -eq "`t")) {
            if ($started) { $null = $tokens.Add($current.ToString()); $null = $current.Clear(); $started = $false }
            continue
        }
        $null = $current.Append($ch)
        $started = $true
    }
    if ($started) { $null = $tokens.Add($current.ToString()) }
    # The comma is load-bearing. PowerShell unrolls a returned array, so an
    # empty token list would come back as $null and a single token as a bare
    # string -- and an install that exited 0 while registering no service is
    # exactly the case this grader has to REPORT rather than crash on.
    return ,$tokens.ToArray()
}

function Get-W4PathArgument {
    <#
        The value of a `--name` argument, or $null when the argument is absent
        or is the last token (an option with no value is not an option that
        names a path).
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] [AllowEmptyCollection()] [string[]] $Tokens,
        [Parameter(Mandatory = $true)] [string] $Name
    )
    if ($null -eq $Tokens) { return $null }
    for ($i = 0; $i -lt $Tokens.Length - 1; $i++) {
        if ($Tokens[$i] -eq $Name) { return $Tokens[$i + 1] }
    }
    return $null
}

function Test-W4SamePath {
    <#
        Windows paths compare case-insensitively, and a trailing separator is
        not a difference. Nothing here touches the filesystem: the paths being
        compared are the ones the SCM recorded and the ones the installer was
        told to use, and one of them will not exist by the time this runs.
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] [AllowEmptyString()] [string] $Left,
        [Parameter(Mandatory = $true)] [AllowNull()] [AllowEmptyString()] [string] $Right
    )
    # An absent argument arrives here as $null, which PowerShell binds to the
    # empty string. Absent is not equal to anything, including another absent.
    if ([string]::IsNullOrEmpty($Left) -or [string]::IsNullOrEmpty($Right)) { return $false }
    $normalise = { param($p) $p.Trim().TrimEnd('\', '/').Replace('/', '\') }
    return (& $normalise $Left).Equals((& $normalise $Right), [System.StringComparison]::OrdinalIgnoreCase)
}

function Join-W4Path {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $Relative
    )
    return ($Root.TrimEnd('\', '/') + '\' + $Relative.Replace('/', '\').TrimStart('\'))
}

function Get-W4FailureAction {
    <#
        One entry of the failure-action array the SCM reported, or $null when
        the service has no policy at all or the array is shorter than the index
        asked for. Absent is never equal to anything: a policy with two entries
        has no third entry, and "the third entry is not a restart" must NOT be
        satisfied by there being no third entry -- the SCM repeats the LAST
        configured action for every failure past the end of the array, so a
        two-entry array of restarts restarts forever.
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] $FailureActions,
        [Parameter(Mandatory = $true)] [int] $Index
    )
    if ($null -eq $FailureActions) { return $null }
    if (-not $FailureActions.ContainsKey('actions')) { return $null }
    $actions = @($FailureActions.actions)
    if ($Index -lt 0 -or $Index -ge $actions.Count) { return $null }
    return $actions[$Index]
}

function Test-W4FailureAction {
    <#
        Does entry $Index of the reported policy match the §4 row at the same
        index, in BOTH halves -- the action the SCM will take and the delay
        before it takes it? A gate that compared only the action would call
        `restart after 1 s` correct, and a restart loop with a one-second delay
        is the thing §4's 60 s exists to rule out.
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] $FailureActions,
        [Parameter(Mandatory = $true)] [int] $Index
    )
    $observed = Get-W4FailureAction -FailureActions $FailureActions -Index $Index
    if ($null -eq $observed) { return $false }
    if ($Index -ge $script:ContractFailureActions.Count) { return $false }
    $expected = $script:ContractFailureActions[$Index]
    if (-not $observed.ContainsKey('type') -or -not $observed.ContainsKey('delayMs')) { return $false }
    return (([string]$observed.type -ceq [string]$expected.type) -and
        ([int]$observed.delayMs -eq [int]$expected.delayMs))
}

function Get-W4Predicates {
    <#
        Grade one observation. Every predicate is answered for every run,
        including the ones a control is expected to redden, because a control
        that reddens its own predicate is only evidence if the OTHER predicates
        stayed green under the same mutation.

        The observation is a hashtable with these keys:

          msiFileNames                 [string[]] long names from the MSI File table
          installPrefix                [string]   where the package was told to install
          programDataRoot              [string]   %ProgramData%\Tesserafin\Server
          serverRelativeExe            [string]   from the accepted W2-A5 script
          webRelativeDir               [string]   from the accepted W2-A5 script
          ffmpegRelativeExe            [string]   from the accepted W2-A5 script
          programFilesTesserafinExists [bool]     %ProgramFiles%\Tesserafin after install
          installedServerExe           [bool]     the three layout members, on disk
          installedWebDir              [bool]
          installedFfmpegExe           [bool]
          service                      [hashtable] or $null -- the SCM registry key
                                       after install: ImagePath, Start,
                                       DelayedAutostart, ObjectName, DisplayName,
                                       Description
          serviceState                 [string]   'Stopped' / 'Running' / 'Absent'
          failureActions               [hashtable] or $null -- what the SCM
                                       reported through QueryServiceConfig2W
                                       after the install:
                                         resetPeriodSeconds [int]
                                         actions [array] of
                                           @{ type = [string]; delayMs = [int] }
                                       $null when the service carries no
                                       failure policy at all
          serviceKeyAfterUninstall     [bool]     the SCM key still exists
          filesUnderPrefixAfterUninstall [int]
          stateAfterUninstall          [hashtable] name -> @{ directory = [bool]; sentinel = [bool] }
    #>
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param([Parameter(Mandatory = $true)] [hashtable] $Observation)

    $o = $Observation
    $service = $o.service
    $imagePath = if ($null -ne $service -and $service.ContainsKey('ImagePath')) { [string]$service.ImagePath } else { '' }
    $tokens = Split-W4CommandLine -CommandLine $imagePath

    # The executable the SCM will launch. Windows Installer writes the key file
    # first, quoted or not depending on the path, so the first token is taken
    # rather than the string being matched from the left.
    $imageExe = if ($null -ne $tokens -and $tokens.Length -gt 0) { $tokens[0] } else { $null }

    $expectedExe = Join-W4Path -Root $o.installPrefix -Relative $o.serverRelativeExe
    $expectedWeb = Join-W4Path -Root $o.installPrefix -Relative $o.webRelativeDir
    $expectedFfmpeg = Join-W4Path -Root $o.installPrefix -Relative $o.ffmpegRelativeExe

    $stateNames = @('config', 'data', 'cache', 'log')
    $stateDirsSurvived = $true
    $stateSentinelsSurvived = $true
    foreach ($name in $stateNames) {
        if (-not $o.stateAfterUninstall.ContainsKey($name)) { $stateDirsSurvived = $false; $stateSentinelsSurvived = $false; continue }
        if (-not $o.stateAfterUninstall[$name].directory) { $stateDirsSurvived = $false }
        if (-not $o.stateAfterUninstall[$name].sentinel) { $stateSentinelsSurvived = $false }
    }

    $predicates = [ordered]@{}

    # ── the package contains the accepted layout ────────────────────────────
    $predicates['msiContainsServerExe'] = [bool](@($o.msiFileNames) -contains $o.serverRelativeExe)
    $predicates['installedServerExe'] = [bool]$o.installedServerExe
    $predicates['installedWebDir'] = [bool]$o.installedWebDir
    $predicates['installedFfmpegExe'] = [bool]$o.installedFfmpegExe
    # The ruling asks which prefix the install actually used. This is that
    # question as a predicate, not as a preference: if the override did not take
    # and the package landed in the runner's real %ProgramFiles%, that is the
    # finding, and nothing here silently accepts it.
    $predicates['installedOutsideProgramFiles'] = -not [bool]$o.programFilesTesserafinExists

    # ── the service, exactly as W0 §4 specifies it ──────────────────────────
    $predicates['serviceRegistered'] = ($null -ne $service)
    $predicates['serviceImagePathIsInstalledExe'] = (Test-W4SamePath -Left $imageExe -Right $expectedExe)
    $predicates['serviceImagePathHasServiceFlag'] = ($null -ne $tokens -and $tokens -contains '--service')
    $predicates['serviceImagePathHasConfigDir'] = (Test-W4SamePath `
        -Left (Get-W4PathArgument -Tokens $tokens -Name '--configdir') `
        -Right (Join-W4Path -Root $o.programDataRoot -Relative 'config'))
    $predicates['serviceImagePathHasDataDir'] = (Test-W4SamePath `
        -Left (Get-W4PathArgument -Tokens $tokens -Name '--datadir') `
        -Right (Join-W4Path -Root $o.programDataRoot -Relative 'data'))
    $predicates['serviceImagePathHasCacheDir'] = (Test-W4SamePath `
        -Left (Get-W4PathArgument -Tokens $tokens -Name '--cachedir') `
        -Right (Join-W4Path -Root $o.programDataRoot -Relative 'cache'))
    $predicates['serviceImagePathHasLogDir'] = (Test-W4SamePath `
        -Left (Get-W4PathArgument -Tokens $tokens -Name '--logdir') `
        -Right (Join-W4Path -Root $o.programDataRoot -Relative 'log'))
    $predicates['serviceImagePathHasWebDir'] = (Test-W4SamePath `
        -Left (Get-W4PathArgument -Tokens $tokens -Name '--webdir') -Right $expectedWeb)
    $predicates['serviceImagePathHasFfmpeg'] = (Test-W4SamePath `
        -Left (Get-W4PathArgument -Tokens $tokens -Name '--ffmpeg') -Right $expectedFfmpeg)
    $predicates['serviceStartIsAutomatic'] =
        ($null -ne $service -and [int]$service.Start -eq $script:ServiceAutoStart)
    $predicates['serviceStartIsDelayed'] =
        ($null -ne $service -and [int]$service.DelayedAutostart -eq 1)
    $predicates['serviceAccountIsVirtualAccount'] =
        ($null -ne $service -and ([string]$service.ObjectName).Equals('NT SERVICE\Tesserafin', [System.StringComparison]::OrdinalIgnoreCase))
    $predicates['serviceDisplayNameIsContract'] =
        ($null -ne $service -and [string]$service.DisplayName -ceq $script:ContractDisplayName)
    $predicates['serviceDescriptionIsContract'] =
        ($null -ne $service -and [string]$service.Description -ceq $script:ContractDescription)
    # W0 §10: a fresh installation leaves the service installed and enabled but
    # NOT started -- an operator decides when a media server begins serving.
    $predicates['serviceNotStartedByInstall'] = ([string]$o.serviceState -eq 'Stopped')

    # ── W0 §4's recovery row, read back from the SCM (W4-A2) ────────────────
    # `failureActions` is absent from a W4-A0-era observation, so the key is
    # asked for rather than indexed: StrictMode 3 makes a missing key an error,
    # and a grader that threw would be indistinguishable from a policy that was
    # never applied.
    $failureActions = $(if ($o.ContainsKey('failureActions')) { $o.failureActions } else { $null })

    $predicates['serviceFailureActionsConfigured'] = ($null -ne $failureActions)
    $predicates['serviceFailureResetPeriodIsContract'] =
        ($null -ne $failureActions -and $failureActions.ContainsKey('resetPeriodSeconds') -and
            [int]$failureActions.resetPeriodSeconds -eq $script:ContractResetPeriodSeconds)
    # EXACTLY three. Not "at least three": the SCM repeats the last configured
    # action for every failure beyond the end of the array, so a fourth entry
    # would change what the fourth failure does, and a trimmed third would turn
    # the second restart into an indefinite one. Either way the crash loop W0 §4
    # wants visible stops being visible.
    $predicates['serviceFailureActionCountIsContract'] =
        ($null -ne $failureActions -and $failureActions.ContainsKey('actions') -and
            @($failureActions.actions).Count -eq $script:ContractFailureActions.Count)
    $predicates['serviceFailureFirstIsRestartAfter60s'] =
        (Test-W4FailureAction -FailureActions $failureActions -Index 0)
    $predicates['serviceFailureSecondIsRestartAfter60s'] =
        (Test-W4FailureAction -FailureActions $failureActions -Index 1)
    $predicates['serviceFailureThirdIsNoAction'] =
        (Test-W4FailureAction -FailureActions $failureActions -Index 2)

    # ── uninstall ───────────────────────────────────────────────────────────
    $predicates['uninstallRemovedService'] = -not [bool]$o.serviceKeyAfterUninstall
    $predicates['uninstallRemovedBinaries'] = ([int]$o.filesUnderPrefixAfterUninstall -eq 0)
    $predicates['stateDirectoriesSurvivedUninstall'] = $stateDirsSurvived
    $predicates['stateSentinelsSurvivedUninstall'] = $stateSentinelsSurvived

    return $predicates
}

function Get-W4ControlExpectations {
    <#
        The RED set each hostile control must produce -- exactly, no more and no
        less. `no-exe` reddens three because renaming the delivered executable
        is one defect with three visible consequences; a control whose declared
        set were only the first would pass while quietly breaking the service's
        ImagePath as well.
    #>
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param()
    return [ordered]@{
        'no-exe' = @(
            'msiContainsServerExe'
            'installedServerExe'
            'serviceImagePathIsInstalledExe'
        )
        'no-service-flag' = @('serviceImagePathHasServiceFlag')
        'no-path-flags' = @(
            'serviceImagePathHasWebDir'
            'serviceImagePathHasFfmpeg'
        )
        'no-service-remove' = @('uninstallRemovedService')
        # W4-A2. `no-util-config` reddens six because a service with no
        # failure policy at all has no reset period, no count and no first,
        # second or third entry -- one defect with six visible consequences,
        # declared in full for the same reason `no-exe` declares three. A
        # declared set of only the first would pass while the grader quietly
        # stopped answering the other five.
        'no-util-config' = @(
            'serviceFailureActionsConfigured'
            'serviceFailureResetPeriodIsContract'
            'serviceFailureActionCountIsContract'
            'serviceFailureFirstIsRestartAfter60s'
            'serviceFailureSecondIsRestartAfter60s'
            'serviceFailureThirdIsNoAction'
        )
        # Both of these leave a complete, well-formed three-entry policy behind
        # and change ONE thing about it.
        #
        # `delay-not-60s` declares TWO because W4-A2-R1 (#234) moved the
        # authoring to `util:ServiceConfig`, which carries a single
        # `RestartServiceDelayInSeconds` that its custom action multiplies into
        # SC_ACTION.Delay for EVERY restart entry. There is no shape of that
        # element in which only the first restart's delay is wrong, so a
        # declared set of one would be a set this control can never produce --
        # one defect, two visible consequences, declared in full the way
        # `no-exe` declares three. The delay is still the half that changed:
        # both entries are still restarts, so a gate that asked only which
        # ACTION the SCM recorded would call this package correct.
        'delay-not-60s' = @(
            'serviceFailureFirstIsRestartAfter60s'
            'serviceFailureSecondIsRestartAfter60s'
        )
        'third-action-restart' = @('serviceFailureThirdIsNoAction')
    }
}

function Get-W4Verdict {
    <#
        Grade one run against what it was supposed to prove.

        For the real package (`none`) every predicate must be green.
        For a hostile control the RED set must be EXACTLY its declared set: a
        control that reddens more than it declared has broken something else as
        well and is not attributable, and one that reddens less has not
        reproduced the defect it exists to reproduce.
    #>
    param(
        [Parameter(Mandatory = $true)] [System.Collections.Specialized.OrderedDictionary] $Predicates,
        [Parameter(Mandatory = $true)] [string] $Mutation
    )

    $red = @($Predicates.Keys | Where-Object { -not $Predicates[$_] })
    $expectations = Get-W4ControlExpectations

    if ($Mutation -eq 'none') {
        $expectedRed = @()
    } elseif ($expectations.Contains($Mutation)) {
        $expectedRed = @($expectations[$Mutation])
    } else {
        return [ordered]@{
            mutation = $Mutation; passed = $false; red = $red; expectedRed = @()
            detail = "no declared expectation for mutation '$Mutation'"
        }
    }

    $unexpected = @($red | Where-Object { $expectedRed -notcontains $_ })
    $missing = @($expectedRed | Where-Object { $red -notcontains $_ })
    $passed = ($unexpected.Count -eq 0 -and $missing.Count -eq 0)

    $detail = if ($passed -and $Mutation -eq 'none') {
        'every predicate green'
    } elseif ($passed) {
        "reddened exactly its declared set: $($expectedRed -join ', ')"
    } else {
        $parts = @()
        if ($unexpected.Count -gt 0) { $parts += "unexpectedly red: $($unexpected -join ', ')" }
        if ($missing.Count -gt 0) { $parts += "expected red but green: $($missing -join ', ')" }
        $parts -join '; '
    }

    return [ordered]@{
        mutation = $Mutation
        passed = $passed
        red = $red
        expectedRed = $expectedRed
        unexpectedlyRed = $unexpected
        expectedRedButGreen = $missing
        detail = $detail
    }
}

Export-ModuleMember -Function Split-W4CommandLine, Get-W4PathArgument, Test-W4SamePath,
    Join-W4Path, Get-W4FailureAction, Test-W4FailureAction, Get-W4Predicates,
    Get-W4ControlExpectations, Get-W4Verdict
