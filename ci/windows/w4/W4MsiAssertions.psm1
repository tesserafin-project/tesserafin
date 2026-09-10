#Requires -Version 7.2
<#
    W4-A0 (#234), extended by W4-A2 with the W0 §4 recovery row and by W4-A3
    with the W0 §9.3 ACLs. The predicates the MSI skeleton is graded on, and the
    RED set each hostile control is expected to produce.

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

# ---------------------------------------------------------------------------
# W0 §9.3, the ACL contract, restated ONCE for the grader (W4-A3).
#
#   %ProgramFiles%\Tesserafin\Server    NT SERVICE\Tesserafin: read and execute
#                                       ONLY -- the service must not be able to
#                                       rewrite its own binaries or its own
#                                       FFmpeg
#   %ProgramData%\Tesserafin\Server\    NT SERVICE\Tesserafin: Modify, on each
#     config, data, cache, log          of the four
#   all of the above                    Administrators and SYSTEM: Full.
#                                       Users: no inherited write
#   inheritance broken at               %ProgramData%\Tesserafin\
#
# Everything here is a SID and an integer access mask, and NOTHING here calls
# into System.Security.Principal or System.Security.AccessControl. Those types
# are Windows-only, and this module is graded on Linux by
# `ci/windows/w4/assertion-self-test.ps1` before a runner is ever asked for. The
# probe does the extraction on the host; this file only does arithmetic.
# ---------------------------------------------------------------------------

# `NT SERVICE\Tesserafin`. A virtual service account's SID is S-1-5-80 followed
# by the SHA-1 of the UPPER-CASE UTF-16LE service name read as five
# little-endian DWORDs, so it is the same value on every machine and it exists
# before the service does. It is stated here as a literal and recomputed from
# the service name by `ci/windows/w4/msi-controls.py`, so neither file can drift
# without the other saying so.
$script:ServiceAccountSid = 'S-1-5-80-761762137-1691453069-3789821951-3290391601-3361247659'

$script:SidAdministrators = 'S-1-5-32-544'
$script:SidLocalSystem = 'S-1-5-18'

# The identities §9.3 means by `Users`. All four are asked about rather than
# just BUILTIN\Users, because `Authenticated Users` and `Everyone` are the two
# ways the same grant is usually written and neither is narrower.
$script:UnprivilegedSids = @(
    'S-1-5-32-545'   # BUILTIN\Users
    'S-1-5-11'       # NT AUTHORITY\Authenticated Users
    'S-1-1-0'        # Everyone
    'S-1-5-32-546'   # BUILTIN\Guests
)

# File-specific access masks, in the units Get-Acl reports FileSystemRights in.
$script:RightsFullControl = 0x1F01FF
$script:RightsModify = 0x1301BF
$script:RightsReadExecute = 0x1200A9

# Every bit that lets the holder change something. FILE_WRITE_DATA,
# FILE_APPEND_DATA, FILE_WRITE_EA, FILE_DELETE_CHILD, FILE_WRITE_ATTRIBUTES,
# DELETE, WRITE_DAC, WRITE_OWNER, and the two generic aliases that would
# otherwise smuggle all of them past a mask comparison.
$script:RightsWriteMask =
    0x00000002 -bor 0x00000004 -bor 0x00000010 -bor 0x00000040 -bor 0x00000100 -bor
    0x00010000 -bor 0x00040000 -bor 0x00080000 -bor 0x10000000 -bor 0x40000000

$script:StateDirectoryNames = @('config', 'data', 'cache', 'log')

function Test-W4HasKey {
    <#
        Does $Bag carry $Key? Asked through IDictionary rather than through
        `ContainsKey`, because the two dictionary shapes this module is handed
        do NOT share that method: the probe builds its ACL observations as
        [ordered], which is a System.Collections.Specialized.OrderedDictionary
        and has `Contains` and no `ContainsKey` at all, while the older
        observations are plain hashtables. Both implement IDictionary.

        This is not defensive tidiness. `ContainsKey` on an [ordered] throws a
        method-not-found under StrictMode, on the runner, two hours in, and
        never on any synthetic observation written as a hashtable -- so the
        self-test builds its ACL observations as [ordered] too.
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] $Bag,
        [Parameter(Mandatory = $true)] [string] $Key
    )
    if ($null -eq $Bag) { return $false }
    if ($Bag -is [System.Collections.IDictionary]) { return $Bag.Contains($Key) }
    return $false
}

function Get-W4Acl {
    <#
        One directory's ACL observation out of the observation hashtable, or
        $null when the probe could not read it -- which is what a directory the
        install never created looks like, and which must never grade as "the
        contract holds". StrictMode 3 makes a missing key an error, so the key
        is asked for rather than indexed.
    #>
    param(
        [Parameter(Mandatory = $true)] [hashtable] $Observation,
        [Parameter(Mandatory = $true)] [string] $Label
    )
    if (-not (Test-W4HasKey -Bag $Observation -Key 'acls')) { return $null }
    $acls = $Observation.acls
    if (-not (Test-W4HasKey -Bag $acls -Key $Label)) { return $null }
    return $acls[$Label]
}

function Get-W4AllowMask {
    <#
        The union of every ALLOW mask the ACL carries for any of $Sids,
        inherited or explicit. Zero when the ACL is missing or grants them
        nothing.

        DENY is deliberately not subtracted. Nothing this package authors denies
        anything, so a deny ACE appearing at all is a finding rather than a
        subtlety, and it is reported by `Test-W4NoDeny` below instead of being
        quietly folded into a number.
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] $Acl,
        [Parameter(Mandatory = $true)] [string[]] $Sids
    )
    if (-not (Test-W4HasKey -Bag $Acl -Key 'rules')) { return 0 }
    $mask = 0
    foreach ($rule in @($Acl.rules)) {
        if (-not (Test-W4HasKey -Bag $rule -Key 'sid')) { continue }
        if (-not (Test-W4HasKey -Bag $rule -Key 'rights')) { continue }
        if ($Sids -notcontains [string]$rule.sid) { continue }
        $type = $(if (Test-W4HasKey -Bag $rule -Key 'type') { [string]$rule.type } else { 'Allow' })
        if ($type -ne 'Allow') { continue }
        $mask = $mask -bor [int]$rule.rights
    }
    return $mask
}

function Test-W4Grants {
    <#
        Does the ACL grant $Sid at least every bit of $Required? A superset is
        accepted for Administrators and SYSTEM -- Full IS the superset -- and for
        the service account's Modify, because §9.3 states a floor. The one place
        a superset is NOT accepted is the read-and-execute grant on
        INSTALLFOLDER, and that is graded as a separate absence-of-write
        predicate rather than by an equality that a harmless extra bit would
        break.
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] $Acl,
        [Parameter(Mandatory = $true)] [string] $Sid,
        [Parameter(Mandatory = $true)] [int] $Required
    )
    if ($null -eq $Acl) { return $false }
    $mask = Get-W4AllowMask -Acl $Acl -Sids @($Sid)
    return (($mask -band $Required) -eq $Required)
}

function Test-W4NoWriteFor {
    <#
        Does the ACL grant NONE of $Sids any bit that would let them change
        something? A missing ACL is $false, never $true: an unreadable directory
        must not satisfy "nobody can write to it".
    #>
    param(
        [Parameter(Mandatory = $true)] [AllowNull()] $Acl,
        [Parameter(Mandatory = $true)] [string[]] $Sids
    )
    if ($null -eq $Acl) { return $false }
    return ((Get-W4AllowMask -Acl $Acl -Sids $Sids) -band $script:RightsWriteMask) -eq 0
}

function Test-W4EveryStateDirectory {
    <#
        Answer one question about all four W0 §9.1 state directories at once.
        The §9.3 row is stated for the four together, and a predicate per
        directory would give one defect four visible consequences and make every
        control declare four names.
    #>
    param(
        [Parameter(Mandatory = $true)] [hashtable] $Observation,
        [Parameter(Mandatory = $true)] [scriptblock] $Test
    )
    foreach ($name in $script:StateDirectoryNames) {
        $acl = Get-W4Acl -Observation $Observation -Label $name
        if ($null -eq $acl) { return $false }
        if (-not (& $Test $acl)) { return $false }
    }
    return $true
}

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
          acls                         [ordered] or absent -- W4-A3. Label ->
                                       @{ path; protected [bool]; owner;
                                          sddl; rules = @( @{ sid; rights [int];
                                          type; inherited; inheritanceFlags;
                                          propagationFlags } ) }, read AFTER the
                                       install and BEFORE the uninstall. The
                                       labels are `installFolder`, `dataRoot`
                                       (%ProgramData%\Tesserafin, the directory
                                       inheritance is broken at), `server` (the
                                       intermediate directory above the state
                                       tree) and the four state directory names. A label whose
                                       value is $null is a directory the probe
                                       could not read.
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

    # ── W0 §9.3's ACLs, read back off the installed layout (W4-A3) ──────────
    # Read from `Get-Acl` after the install, never from the authoring, and never
    # by starting the service: W0 §9.2 measured that the default %ProgramData%
    # ACL is already permissive enough for the service to run, so a service that
    # starts proves nothing at all about these grants.
    $installFolderAcl = Get-W4Acl -Observation $o -Label 'installFolder'
    $dataRootAcl = Get-W4Acl -Observation $o -Label 'dataRoot'

    # The service can run the binaries it was installed with...
    $predicates['installFolderServiceCanReadAndExecute'] =
        (Test-W4Grants -Acl $installFolderAcl -Sid $script:ServiceAccountSid `
            -Required $script:RightsReadExecute)
    # ...and cannot rewrite them. W0 §9.3 states this as the reason the grant
    # exists, so it is graded as its own predicate: a Modify grant satisfies
    # "read and execute" and would leave the row above green on its own.
    $predicates['installFolderServiceCannotWrite'] =
        (Test-W4NoWriteFor -Acl $installFolderAcl -Sids @($script:ServiceAccountSid))

    # W4-A3-R1 (#234). These three used to be inherited from the runner's temp
    # tree under a disposable prefix, so grading them would have graded
    # RUNNER_TEMP and the probe only recorded them. Run 34500789866 measured
    # that a descriptor applied through MsiLockPermissionsEx becomes the
    # object's WHOLE DACL -- the install died 1310 for exactly that reason --
    # so the package authors these rows itself now, and what the package
    # authors is graded.
    $predicates['installFolderAdministratorsHaveFull'] =
        (Test-W4Grants -Acl $installFolderAcl -Sid $script:SidAdministrators `
            -Required $script:RightsFullControl)
    $predicates['installFolderSystemHasFull'] =
        (Test-W4Grants -Acl $installFolderAcl -Sid $script:SidLocalSystem `
            -Required $script:RightsFullControl)
    $predicates['installFolderUsersHaveNoWrite'] =
        (Test-W4NoWriteFor -Acl $installFolderAcl -Sids $script:UnprivilegedSids)

    # The whole slice, in one bit: a protected DACL at %ProgramData%\Tesserafin\
    # so the permissive parent cannot widen access to the database.
    $predicates['dataRootInheritanceBroken'] =
        ((Test-W4HasKey -Bag $dataRootAcl -Key 'protected') -and [bool]$dataRootAcl.protected)

    $predicates['stateDirectoriesServiceHasModify'] = (Test-W4EveryStateDirectory -Observation $o -Test {
        param($acl) Test-W4Grants -Acl $acl -Sid $script:ServiceAccountSid -Required $script:RightsModify })
    $predicates['stateDirectoriesAdministratorsHaveFull'] = (Test-W4EveryStateDirectory -Observation $o -Test {
        param($acl) Test-W4Grants -Acl $acl -Sid $script:SidAdministrators -Required $script:RightsFullControl })
    $predicates['stateDirectoriesSystemHasFull'] = (Test-W4EveryStateDirectory -Observation $o -Test {
        param($acl) Test-W4Grants -Acl $acl -Sid $script:SidLocalSystem -Required $script:RightsFullControl })
    $predicates['stateDirectoriesUsersHaveNoWrite'] = (Test-W4EveryStateDirectory -Observation $o -Test {
        param($acl) Test-W4NoWriteFor -Acl $acl -Sids $script:UnprivilegedSids })

    # W4-A3-R2 (#234). `%ProgramData%\Tesserafin\Server` is not a row in W0
    # §9.3's table, and it is measured anyway. Without a descriptor of its own it
    # keeps %ProgramData%'s `Users` write, and the protected parent does not save
    # it: `Users` hold SeChangeNotifyPrivilege by default, and
    # bypass-traverse-checking takes the access decision straight to the leaf. It
    # is graded on the one row that makes it a defect rather than on all four --
    # the service and administrative rows are carried by the same descriptor the
    # four state directories get and are already proven there.
    $predicates['serverDirectoryUsersHaveNoWrite'] =
        (Test-W4NoWriteFor -Acl (Get-W4Acl -Observation $o -Label 'server') `
            -Sids $script:UnprivilegedSids)

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
        # W4-A3, the ACL controls. Each changes ONE thing about the W0 §9.3
        # contract, and every variant still grants Administrators and SYSTEM
        # Full, so no control reddens the administrative rows as collateral.
        #
        # W4-A3-R2 (#234) WITHDREW `acl-not-protected`, which removed the `P`
        # from the data root's descriptor and declared `dataRootInheritanceBroken`.
        # Run 34502732425 measured that MsiLockPermissionsEx writes a PROTECTED
        # DACL whatever the SDDL asks for -- `InstallFolderSddl` is authored
        # `D:` and comes back `D:P`, and so did that mutant -- so the control
        # graded "expected red but green" and could never do otherwise. A control
        # that cannot fail is not a control. `dataRootInheritanceBroken` is still
        # measured and still asserted; it is simply not falsifiable from this
        # authoring, and saying so is better than a mutant that pretends it is.
        #
        # The other two now mutate the descriptor the state directories actually
        # GET, which since R2 is their own and not one inherited from the data
        # root. `acl-users-write` declares two because one descriptor is what all
        # five operator-tree directories are given: the four W0 §9.3 names and
        # `Server` above them.
        'acl-users-write' = @(
            'stateDirectoriesUsersHaveNoWrite'
            'serverDirectoryUsersHaveNoWrite'
        )
        'acl-no-service-grant' = @('stateDirectoriesServiceHasModify')
        # Read and execute still hold, so this reddens the write half alone.
        'acl-install-writable' = @('installFolderServiceCannotWrite')
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

# ===========================================================================
# W4-A4 (#234): the MajorUpgrade grader.
#
# The same doctrine as everything above it. PURE: two observation hashtables in
# -- one describing the two PACKAGES, one describing the machine AFTER the
# second one was installed over the first -- and an ordered predicate map out.
# No msiexec, no registry, no filesystem, so `assertion-self-test.ps1` can drive
# every control on any host before the hosted job spends runner time.
#
# W4-A4 grades what the ruling names and nothing it does not: the binaries under
# INSTALLFOLDER are the SECOND package's, the sentinels written between the two
# installs are still there byte for byte, the service is still registered and
# still Stopped with the §4 binPath, the §9.3 descriptors still hold, and the
# UpgradeCode bytes did not move. The §4 recovery row and the §9.3 descriptors
# are graded again rather than assumed: W4-A2 and W4-A3 measured them on a FRESH
# install, and "an upgrade preserves them" is a different statement about a
# different sequence of standard actions.
#
# What it deliberately does NOT grade: repair, advertised repair, downgrade, the
# Event Log source, signing, and anything about a started service. The W4-A4
# ruling excludes all of them.
# ===========================================================================

# W4-A1 froze this and W4-A4 reads it back off the built packages rather than
# off the authoring, because a `Property` table row is what the machine actually
# matches an installed product against. Windows Installer stores it braced and
# upper-case, so the comparison below normalises before it compares -- the
# AUTHORING gate in `ci/windows/w4/msi-controls.py` is the ordinal one.
$script:FrozenUpgradeCode = '0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05'

function Get-W4NormalisedGuid {
    <#
        A Windows Installer GUID as the frozen string is written: no braces,
        lower case. `$null` in, `$null` out, so a package that carries no such
        Property row is distinguishable from one that carries a different value.
    #>
    param([Parameter()] [AllowNull()] [string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    return $Value.Trim().Trim('{', '}').ToLowerInvariant()
}

function Test-W4SameDigest {
    <#
        Two SHA-256 hex digests, compared as digests rather than as strings: a
        digest that is absent is not equal to anything, including another
        absent one, because "the file was not there" must never grade as "the
        file was the one we expected".
    #>
    param([Parameter()] [AllowNull()] [string] $Left,
          [Parameter()] [AllowNull()] [string] $Right)
    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
    return $Left.Trim().ToLowerInvariant() -ceq $Right.Trim().ToLowerInvariant()
}

function Get-W4UpgradePredicates {
    <#
        Grade one A -> B pair.

        The observation is a hashtable with these keys:

          aMsi, bMsi          [hashtable] read out of the BUILT packages' own
                              tables, never out of the authoring:
                                productCode      [string] Property/ProductCode
                                productVersion   [string] Property/ProductVersion
                                upgradeCode      [string] Property/UpgradeCode
                                stagedExeSha256  [string] the tesserafin.exe the
                                                 package was built from
                                startsServiceOnInstall [bool] the ServiceControl
                                                 table carries a start-on-install
                                                 event for the Tesserafin service
          upgradeExit         [int]      msiexec's exit code for B over A
          installPrefix       [string]   INSTALLFOLDER, passed to BOTH installs
          programDataRoot     [string]   %ProgramData%\Tesserafin\Server
          serverRelativeExe   [string]   the three, from the accepted W2-A5 script
          webRelativeDir      [string]
          ffmpegRelativeExe   [string]
          installedExeSha256  [string]   the exe under INSTALLFOLDER after B
          installedServerExe / installedWebDir / installedFfmpegExe [bool]
          service             [hashtable] or $null -- the SCM registry key after B
          serviceState        [string]   'Stopped' / 'Running' / 'Absent'
          failureActions      [hashtable] or $null -- QueryServiceConfig2W after B
          acls                [ordered]  Get-Acl observations after B
          stateAfterUpgrade   [hashtable] name -> @{ directory [bool];
                              sentinel [bool]; sha256 [string] }
          sentinelSha256      [string]   what was written into all four state
                              directories BETWEEN the two installs
          aProductInstalled   [bool]     A's ProductCode still installed after B
          bProductInstalled   [bool]     B's ProductCode installed after B
    #>
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param([Parameter(Mandatory = $true)] [hashtable] $Observation)

    $o = $Observation
    $a = $o.aMsi
    $b = $o.bMsi
    $service = $o.service
    $imagePath = if ($null -ne $service -and $service.ContainsKey('ImagePath')) { [string]$service.ImagePath } else { '' }
    $tokens = Split-W4CommandLine -CommandLine $imagePath
    $imageExe = if ($null -ne $tokens -and $tokens.Length -gt 0) { $tokens[0] } else { $null }

    $expectedExe = Join-W4Path -Root $o.installPrefix -Relative $o.serverRelativeExe
    $expectedWeb = Join-W4Path -Root $o.installPrefix -Relative $o.webRelativeDir
    $expectedFfmpeg = Join-W4Path -Root $o.installPrefix -Relative $o.ffmpegRelativeExe

    $aUpgradeCode = Get-W4NormalisedGuid -Value ($(if ($a.ContainsKey('upgradeCode')) { $a.upgradeCode } else { $null }))
    $bUpgradeCode = Get-W4NormalisedGuid -Value ($(if ($b.ContainsKey('upgradeCode')) { $b.upgradeCode } else { $null }))

    $stateNames = @('config', 'data', 'cache', 'log')
    $stateDirsSurvived = $true
    $stateSentinelsSurvived = $true
    $stateSentinelsUnchanged = $true
    foreach ($name in $stateNames) {
        if (-not $o.stateAfterUpgrade.ContainsKey($name)) {
            $stateDirsSurvived = $false; $stateSentinelsSurvived = $false
            $stateSentinelsUnchanged = $false; continue
        }
        $entry = $o.stateAfterUpgrade[$name]
        if (-not $entry.directory) { $stateDirsSurvived = $false }
        if (-not $entry.sentinel) { $stateSentinelsSurvived = $false }
        $digest = $(if ($entry.ContainsKey('sha256')) { $entry.sha256 } else { $null })
        if (-not (Test-W4SameDigest -Left $digest -Right $o.sentinelSha256)) { $stateSentinelsUnchanged = $false }
    }

    $predicates = [ordered]@{}

    # ── the two packages are an upgrade pair, and only that ─────────────────
    # W4-A1's frozen GUID, read out of BOTH packages. This is the ruling's
    # "UpgradeCode bytes unchanged", answered from the artifacts rather than
    # from the file they were built from.
    $predicates['upgradeCodeIsFrozenInA'] = ($aUpgradeCode -ceq $script:FrozenUpgradeCode)
    $predicates['upgradeCodeIsFrozenInB'] = ($bUpgradeCode -ceq $script:FrozenUpgradeCode)
    $predicates['upgradeCodeStable'] =
        ($null -ne $aUpgradeCode -and $null -ne $bUpgradeCode -and $aUpgradeCode -ceq $bUpgradeCode)
    # Two packages sharing an UpgradeCode AND a ProductCode are the same product
    # and B would be a reinstall, not an upgrade -- a sequence that would keep
    # state for a reason that has nothing to do with MajorUpgrade.
    # Both must be PRESENT and different. `-not (equal)` alone would grade two
    # packages that carry no ProductCode at all as a valid upgrade pair, because
    # an absent value is not equal to anything -- including another absent one.
    $aProductCode = Get-W4NormalisedGuid -Value ($(if ($a.ContainsKey('productCode')) { $a.productCode } else { $null }))
    $bProductCode = Get-W4NormalisedGuid -Value ($(if ($b.ContainsKey('productCode')) { $b.productCode } else { $null }))
    $predicates['productCodesDiffer'] =
        ($null -ne $aProductCode -and $null -ne $bProductCode -and $aProductCode -cne $bProductCode)
    $predicates['versionBIsHigher'] = $(
        try { ([version]$b.productVersion) -gt ([version]$a.productVersion) } catch { $false })

    # ── the upgrade itself ──────────────────────────────────────────────────
    $predicates['upgradeInstallSucceeded'] = ([int]$o.upgradeExit -eq 0)
    # MajorUpgrade removed A rather than installing B beside it. A machine
    # carrying both products would keep its state and replace its binaries too,
    # and would still be the defect W0 §10 names.
    $predicates['previousProductRemoved'] = (-not [bool]$o.aProductInstalled)
    $predicates['upgradedProductInstalled'] = [bool]$o.bProductInstalled

    # ── the binaries are B's ────────────────────────────────────────────────
    $predicates['installedServerExe'] = [bool]$o.installedServerExe
    $predicates['installedWebDir'] = [bool]$o.installedWebDir
    $predicates['installedFfmpegExe'] = [bool]$o.installedFfmpegExe
    # Two predicates, not one. "It is not A's any more" and "it is B's" are
    # different statements, and a package that delivered neither would satisfy
    # the first alone.
    $predicates['exeReplaced'] =
        (-not (Test-W4SameDigest -Left $o.installedExeSha256 -Right $a.stagedExeSha256))
    $predicates['exeIsB'] = (Test-W4SameDigest -Left $o.installedExeSha256 -Right $b.stagedExeSha256)

    # ── the operator's state survived ───────────────────────────────────────
    $predicates['stateDirectoriesSurvivedUpgrade'] = $stateDirsSurvived
    $predicates['stateSentinelsSurvivedUpgrade'] = $stateSentinelsSurvived
    # A sentinel that exists is not a sentinel that was kept: an upgrade that
    # deleted and recreated the file would satisfy the row above.
    $predicates['stateSentinelContentsUnchanged'] = $stateSentinelsUnchanged

    # ── the service, still exactly as W0 §4 specifies it ────────────────────
    $predicates['serviceRegisteredAfterUpgrade'] = ($null -ne $service)
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
    $predicates['serviceStoppedAfterUpgrade'] = ([string]$o.serviceState -eq 'Stopped')

    # W0 §10 leaves the service installed and enabled but NOT started, and an
    # upgrade is not the moment to change that. This is graded on BOTH halves
    # deliberately. The live half alone cannot be trusted: a package that asked
    # to start the service inside the transaction and FAILED would roll the
    # whole install back -- W0 §5.2 measured exactly that, 1920 to 1603 -- and
    # the machine would then be left carrying A, with a Stopped service, which
    # is the shape a green live reading has. The table half says what the
    # package ASKED for and reddens whatever the runner did with it.
    $predicates['bDoesNotStartService'] =
        ((-not [bool]$b.startsServiceOnInstall) -and ([string]$o.serviceState -ne 'Running'))

    # ── W0 §4's recovery row, read back from the SCM AFTER the upgrade ──────
    $failureActions = $(if ($o.ContainsKey('failureActions')) { $o.failureActions } else { $null })
    $predicates['serviceFailureActionsConfigured'] = ($null -ne $failureActions)
    $predicates['serviceFailureResetPeriodIsContract'] =
        ($null -ne $failureActions -and $failureActions.ContainsKey('resetPeriodSeconds') -and
            [int]$failureActions.resetPeriodSeconds -eq $script:ContractResetPeriodSeconds)
    $predicates['serviceFailureActionCountIsContract'] =
        ($null -ne $failureActions -and $failureActions.ContainsKey('actions') -and
            @($failureActions.actions).Count -eq $script:ContractFailureActions.Count)
    $predicates['serviceFailureFirstIsRestartAfter60s'] =
        (Test-W4FailureAction -FailureActions $failureActions -Index 0)
    $predicates['serviceFailureSecondIsRestartAfter60s'] =
        (Test-W4FailureAction -FailureActions $failureActions -Index 1)
    $predicates['serviceFailureThirdIsNoAction'] =
        (Test-W4FailureAction -FailureActions $failureActions -Index 2)

    # ── W0 §9.3's ACLs, read back off the UPGRADED layout ───────────────────
    # The six OperatorTreePermissions components are neither Permanent nor
    # NeverOverwrite -- deliberately, and the authoring says why -- so the
    # upgrade re-applies every descriptor here. These rows are B's work, not
    # A's leftovers, and that is the whole reason they can be graded again.
    $installFolderAcl = Get-W4Acl -Observation $o -Label 'installFolder'
    $dataRootAcl = Get-W4Acl -Observation $o -Label 'dataRoot'

    $predicates['installFolderServiceCanReadAndExecute'] =
        (Test-W4Grants -Acl $installFolderAcl -Sid $script:ServiceAccountSid `
            -Required $script:RightsReadExecute)
    $predicates['installFolderServiceCannotWrite'] =
        (Test-W4NoWriteFor -Acl $installFolderAcl -Sids @($script:ServiceAccountSid))
    $predicates['installFolderAdministratorsHaveFull'] =
        (Test-W4Grants -Acl $installFolderAcl -Sid $script:SidAdministrators `
            -Required $script:RightsFullControl)
    $predicates['installFolderSystemHasFull'] =
        (Test-W4Grants -Acl $installFolderAcl -Sid $script:SidLocalSystem `
            -Required $script:RightsFullControl)
    $predicates['installFolderUsersHaveNoWrite'] =
        (Test-W4NoWriteFor -Acl $installFolderAcl -Sids $script:UnprivilegedSids)
    $predicates['dataRootInheritanceBroken'] =
        ((Test-W4HasKey -Bag $dataRootAcl -Key 'protected') -and [bool]$dataRootAcl.protected)
    $predicates['stateDirectoriesServiceHasModify'] = (Test-W4EveryStateDirectory -Observation $o -Test {
        param($acl) Test-W4Grants -Acl $acl -Sid $script:ServiceAccountSid -Required $script:RightsModify })
    $predicates['stateDirectoriesAdministratorsHaveFull'] = (Test-W4EveryStateDirectory -Observation $o -Test {
        param($acl) Test-W4Grants -Acl $acl -Sid $script:SidAdministrators -Required $script:RightsFullControl })
    $predicates['stateDirectoriesSystemHasFull'] = (Test-W4EveryStateDirectory -Observation $o -Test {
        param($acl) Test-W4Grants -Acl $acl -Sid $script:SidLocalSystem -Required $script:RightsFullControl })
    $predicates['stateDirectoriesUsersHaveNoWrite'] = (Test-W4EveryStateDirectory -Observation $o -Test {
        param($acl) Test-W4NoWriteFor -Acl $acl -Sids $script:UnprivilegedSids })
    $predicates['serverDirectoryUsersHaveNoWrite'] =
        (Test-W4NoWriteFor -Acl (Get-W4Acl -Observation $o -Label 'server') `
            -Sids $script:UnprivilegedSids)

    return $predicates
}

function Get-W4UpgradeControlExpectations {
    <#
        The RED set each W4-A4 hostile control must produce -- exactly, no more
        and no less, the same rule the fresh-install controls are held to.

        The ruling names five, and they are the five below. Three of them are
        LIVE pairs: a real A, a real deliberately broken B, a real msiexec
        upgrade, and a real read-back. Two of them are TABLE controls: the
        package is really built and its own tables are really read, but it is
        deliberately never installed, and the probe says so in the evidence.
        The reason is in the ruling rather than in convenience -- a B carrying a
        different UpgradeCode installs BESIDE A, which is the second product the
        ruling forbids inventing, and a B that starts the service inside the
        transaction is the 1920-to-1603 rollback W0 §5.2 measured, which would
        leave A on the machine and hide the defect behind an install failure.
        Either one would grade a control by an outcome that is not the defect.
    #>
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param()
    return [ordered]@{
        # LIVE. B is built from the stage A was built from, so the upgrade
        # delivers A's bytes again. `exeIsB` stays GREEN and must: B's staged
        # executable IS that file, and a control that reddened both would be
        # indistinguishable from a package that delivered no executable at all.
        'upgrade-same-exe' = @('exeReplaced')
        # LIVE. The four directories survive -- the retained components are
        # still Permanent -- and their contents do not, which is what makes this
        # control attributable to the operator's DATA and not to the tree.
        'upgrade-wipes-state' = @(
            'stateSentinelsSurvivedUpgrade'
            'stateSentinelContentsUnchanged'
        )
        # LIVE. One defect, nineteen visible consequences, declared in full for
        # the same reason `no-exe` declares three and `no-util-config` declares
        # six: a service that is not there has no binPath, no start type, no
        # account and no failure policy, and a declared set of only the first
        # would pass while the grader quietly stopped answering the rest.
        'upgrade-no-service' = @(
            'serviceRegisteredAfterUpgrade'
            'serviceImagePathIsInstalledExe'
            'serviceImagePathHasServiceFlag'
            'serviceImagePathHasConfigDir'
            'serviceImagePathHasDataDir'
            'serviceImagePathHasCacheDir'
            'serviceImagePathHasLogDir'
            'serviceImagePathHasWebDir'
            'serviceImagePathHasFfmpeg'
            'serviceStartIsAutomatic'
            'serviceStartIsDelayed'
            'serviceAccountIsVirtualAccount'
            'serviceStoppedAfterUpgrade'
            'serviceFailureActionsConfigured'
            'serviceFailureResetPeriodIsContract'
            'serviceFailureActionCountIsContract'
            'serviceFailureFirstIsRestartAfter60s'
            'serviceFailureSecondIsRestartAfter60s'
            'serviceFailureThirdIsNoAction'
        )
        # TABLE. Only B's Property/UpgradeCode row is moved, so A is still the
        # frozen GUID and only the two-package predicate can go red.
        # `upgradeCodeIsFrozenInB` goes red with it: the mutant's row is not the
        # frozen value either, and declaring one without the other would be a
        # declared set this control can never produce.
        'upgrade-upgradecode' = @(
            'upgradeCodeIsFrozenInB'
            'upgradeCodeStable'
        )
        # TABLE. Only B's ServiceControl start-on-install bit is set.
        'upgrade-starts-service' = @('bDoesNotStartService')
    }
}

function Get-W4UpgradeVerdict {
    <#
        Grade one A -> B pair against what it was supposed to prove. Same rule
        as `Get-W4Verdict`: the real pair must be green everywhere, and a
        hostile control must redden EXACTLY its declared set.
    #>
    param(
        [Parameter(Mandatory = $true)] [System.Collections.Specialized.OrderedDictionary] $Predicates,
        [Parameter(Mandatory = $true)] [string] $Control
    )

    $red = @($Predicates.Keys | Where-Object { -not $Predicates[$_] })
    $expectations = Get-W4UpgradeControlExpectations

    if ($Control -eq 'none') {
        $expectedRed = @()
    } elseif ($expectations.Contains($Control)) {
        $expectedRed = @($expectations[$Control])
    } else {
        return [ordered]@{
            control = $Control; passed = $false; red = $red; expectedRed = @()
            detail = "no declared expectation for control '$Control'"
        }
    }

    $unexpected = @($red | Where-Object { $expectedRed -notcontains $_ })
    $missing = @($expectedRed | Where-Object { $red -notcontains $_ })
    $passed = ($unexpected.Count -eq 0 -and $missing.Count -eq 0)

    $detail = if ($passed -and $Control -eq 'none') {
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
        control = $Control
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
    Get-W4ControlExpectations, Get-W4Verdict, Test-W4HasKey, Get-W4Acl, Get-W4AllowMask,
    Test-W4Grants, Test-W4NoWriteFor, Test-W4EveryStateDirectory,
    Get-W4NormalisedGuid, Test-W4SameDigest, Get-W4UpgradePredicates,
    Get-W4UpgradeControlExpectations, Get-W4UpgradeVerdict
