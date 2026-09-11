#Requires -Version 7.2
<#
.SYNOPSIS
    Drive `W4MsiAssertions.psm1` over synthetic observations and prove the
    grader is not inert -- on any platform, in under a second, with no MSI.

.DESCRIPTION
    W4-A0 (#234). The hostile controls are only evidence if the predicates they
    are supposed to redden are the predicates that actually go red. That
    property cannot be established by the hosted run itself: a grader that
    answered "red" to everything, or "green" to everything, would produce a
    control table that looks exactly as convincing.

    So the grader is exercised here first, against observations written to be
    what each mutated package genuinely produces -- a renamed executable, an
    argument list missing `--service`, an argument list missing the two path
    arguments, an SCM key that survives the uninstall, a service the SCM would
    recover differently, and -- since W4-A3 -- an installed layout whose ACLs are
    not the W0 §9.3 ones. Each must redden EXACTLY its declared set, and the
    correct package must redden nothing.

    W4-A4 (#234) adds a SECOND grader to the same treatment: the MajorUpgrade
    predicates, driven over synthetic A -> B pairs -- an upgrade that redelivered
    the first package's executable, one that emptied the four state directories,
    one that registered no service, one whose second package carries a different
    UpgradeCode, and one whose second package asks the SCM to start the service.
    W4-A5 (#234) adds a sixth: a second package with no remember-property, whose
    upgrade omits INSTALLFOLDER and therefore resolves the default directory.
    The rule is the same and so is the inertness check.

    This is not a substitute for the hosted measurement and proves nothing about
    WiX, msiexec or the SCM. It proves that the instrument reads.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, 'W4MsiAssertions.psm1')) -Force

$prefix = 'D:\a\_temp\w4a0\prefix'
$dataRoot = 'C:\ProgramData\Tesserafin\Server'
$serverExe = 'tesserafin.exe'
$webDir = 'web'
$ffmpegExe = 'ffmpeg\bin\ffmpeg.exe'

function New-SyntheticObservation {
    param([Parameter(Mandatory = $true)] [string] $Mutation)

    # The delivered executable. The `no-exe` control does not delete it -- it
    # delivers it under a different name, so that containment reddens and the
    # argument list does not, which is what makes the control attributable.
    $deliveredExe = if ($Mutation -eq 'no-exe') { 'tesserafin-w4control.exe' } else { $serverExe }

    $arguments = [System.Collections.Generic.List[string]]::new()
    if ($Mutation -ne 'no-service-flag') { $null = $arguments.Add('--service') }
    foreach ($pair in @(@('--configdir', 'config'), @('--datadir', 'data'), @('--cachedir', 'cache'), @('--logdir', 'log'))) {
        $null = $arguments.Add($pair[0])
        $null = $arguments.Add('"' + (Join-W4Path -Root $dataRoot -Relative $pair[1]) + '"')
    }
    if ($Mutation -ne 'no-path-flags') {
        $null = $arguments.Add('--webdir')
        $null = $arguments.Add('"' + (Join-W4Path -Root $prefix -Relative $webDir) + '"')
        $null = $arguments.Add('--ffmpeg')
        $null = $arguments.Add('"' + (Join-W4Path -Root $prefix -Relative $ffmpegExe) + '"')
    }

    $imagePath = '"' + (Join-W4Path -Root $prefix -Relative $deliveredExe) + '" ' + ($arguments -join ' ')

    # W4-A2. What the SCM would report for each package. `no-util-config`
    # leaves the service with no policy at all, which is what the SCM's default
    # is; the other two leave a complete, well-formed three-entry policy and
    # change one thing about it, so the rest of the recovery row stays green
    # under the same mutation.
    #
    # `delay-not-60s` moves BOTH restart delays, and that is not a convenience:
    # since W4-A2-R1 (#234) the authoring is `util:ServiceConfig`, whose single
    # `RestartServiceDelayInSeconds` its custom action multiplies into
    # SC_ACTION.Delay for every restart entry. An observation with only the
    # first delay moved would be one no package this repository can build could
    # ever produce, and the grader would then be proven against a fiction.
    $failureActions = $null
    if ($Mutation -ne 'no-util-config') {
        $restartDelay = $(if ($Mutation -eq 'delay-not-60s') { 1000 } else { 60000 })
        $third = $(if ($Mutation -eq 'third-action-restart') {
            @{ type = 'restartService'; delayMs = $restartDelay }
        } else {
            @{ type = 'none'; delayMs = 0 }
        })
        $failureActions = @{
            resetPeriodSeconds = 86400
            actions = @(
                @{ type = 'restartService'; delayMs = $restartDelay }
                @{ type = 'restartService'; delayMs = $restartDelay }
                $third
            )
        }
    }

    # W4-A3. What Get-Acl would report for each package, written to be what the
    # authored SDDL genuinely produces rather than what would make the grader
    # look right. The masks are the file-specific ones the authoring states:
    # 0x1f01ff Full, 0x1301bf Modify, 0x1200a9 read and execute.
    #
    # Every dictionary here is [ordered], because that is what the probe emits
    # and OrderedDictionary has no `ContainsKey` at all -- a grader written
    # against hashtables passes this file and dies on the runner.
    #
    # `installFolder` carries Administrators, SYSTEM and `Users` rows since
    # W4-A3-R1 (#234), and they are EXPLICIT. They used to be inherited from the
    # runner's temp tree, which is why nothing graded them; the package now
    # authors them, because a descriptor applied through MsiLockPermissionsEx
    # becomes the object's whole DACL and the rows a %ProgramFiles% directory
    # already has do not survive it.
    $serviceSid = 'S-1-5-80-761762137-1691453069-3789821951-3290391601-3361247659'
    $installFolderMask = $(if ($Mutation -eq 'acl-install-writable') { 0x1301BF } else { 0x1200A9 })

    $dataRootRules = [System.Collections.Generic.List[object]]::new()
    $null = $dataRootRules.Add([ordered]@{ sid = 'S-1-5-32-544'; rights = 0x1F01FF; type = 'Allow'; inherited = $false })
    $null = $dataRootRules.Add([ordered]@{ sid = 'S-1-5-18';     rights = 0x1F01FF; type = 'Allow'; inherited = $false })
    $null = $dataRootRules.Add([ordered]@{ sid = $serviceSid; rights = 0x1301BF; type = 'Allow'; inherited = $false })

    # W4-A3-R2. The data root and the operator-tree directories carry SEPARATE
    # descriptors now, and each is PROTECTED -- Windows Installer writes the
    # DACL protected whatever the SDDL asks for, which is what run 34502732425
    # measured and why the `acl-not-protected` control was withdrawn. So every
    # ACE here is explicit and every directory reports protected; nothing in the
    # operator tree inherits anything.
    $stateRules = [System.Collections.Generic.List[object]]::new()
    $null = $stateRules.Add([ordered]@{ sid = 'S-1-5-32-544'; rights = 0x1F01FF; type = 'Allow'; inherited = $false })
    $null = $stateRules.Add([ordered]@{ sid = 'S-1-5-18';     rights = 0x1F01FF; type = 'Allow'; inherited = $false })
    if ($Mutation -ne 'acl-no-service-grant') {
        $null = $stateRules.Add([ordered]@{ sid = $serviceSid; rights = 0x1301BF; type = 'Allow'; inherited = $false })
    }
    if ($Mutation -eq 'acl-users-write') {
        $null = $stateRules.Add([ordered]@{ sid = 'S-1-5-32-545'; rights = 0x1301BF; type = 'Allow'; inherited = $false })
    }
    $stateRules = @($stateRules)

    $acls = [ordered]@{
        installFolder = [ordered]@{
            path = $prefix
            protected = $false
            rules = @(
                [ordered]@{ sid = 'S-1-5-32-544'; rights = 0x1F01FF; type = 'Allow'; inherited = $false }
                [ordered]@{ sid = 'S-1-5-18';     rights = 0x1F01FF; type = 'Allow'; inherited = $false }
                [ordered]@{ sid = 'S-1-5-32-545'; rights = 0x1200A9; type = 'Allow'; inherited = $false }
                [ordered]@{ sid = $serviceSid;    rights = $installFolderMask; type = 'Allow'; inherited = $false }
            )
        }
        dataRoot = [ordered]@{
            path = 'C:\ProgramData\Tesserafin'
            protected = $true
            rules = @($dataRootRules)
        }
        server = [ordered]@{
            path = $dataRoot
            protected = $true
            rules = $stateRules
        }
    }
    foreach ($name in @('config', 'data', 'cache', 'log')) {
        $acls[$name] = [ordered]@{
            path = (Join-W4Path -Root $dataRoot -Relative $name)
            protected = $true
            rules = $stateRules
        }
    }

    return @{
        acls = $acls
        failureActions = $failureActions
        msiFileNames = @($deliveredExe, 'ffmpeg.exe', 'index.html', 'LICENSE')
        installPrefix = $prefix
        programDataRoot = $dataRoot
        serverRelativeExe = $serverExe
        webRelativeDir = $webDir
        ffmpegRelativeExe = $ffmpegExe
        programFilesTesserafinExists = $false
        installedServerExe = ($Mutation -ne 'no-exe')
        installedWebDir = $true
        installedFfmpegExe = $true
        serviceState = 'Stopped'
        service = @{
            ImagePath = $imagePath
            Start = 2
            DelayedAutostart = 1
            ObjectName = 'NT SERVICE\Tesserafin'
            DisplayName = 'Tesserafin Server'
            Description = 'Tesserafin media server. Manage it at http://localhost:8096.'
        }
        serviceKeyAfterUninstall = ($Mutation -eq 'no-service-remove')
        filesUnderPrefixAfterUninstall = 0
        stateAfterUninstall = @{
            config = @{ directory = $true; sentinel = $true }
            data   = @{ directory = $true; sentinel = $true }
            cache  = @{ directory = $true; sentinel = $true }
            log    = @{ directory = $true; sentinel = $true }
        }
    }
}

$mutations = @('none') + @((Get-W4ControlExpectations).Keys)
$failures = 0
$distinctRedSets = @{}

foreach ($mutation in $mutations) {
    $predicates = Get-W4Predicates -Observation (New-SyntheticObservation -Mutation $mutation)
    $verdict = Get-W4Verdict -Predicates $predicates -Mutation $mutation
    $status = if ($verdict.passed) { 'OK  ' } else { 'FAIL' }
    "$status $($mutation.PadRight(18)) $($verdict.detail)"
    if (-not $verdict.passed) { $failures++ }
    $distinctRedSets[($verdict.red -join '|')] = $true
}

# Two controls with identical failure lists is the tell that the harness graded
# nothing. Every mutation here must produce a DIFFERENT red set, including the
# empty one the correct package produces.
if ($distinctRedSets.Count -ne $mutations.Count) {
    "FAIL distinct red sets: $($distinctRedSets.Count) across $($mutations.Count) mutations"
    $failures++
}

# The predicate list itself must not be empty or trivially short: a grader that
# answered one question would satisfy every check above.
$allPredicates = Get-W4Predicates -Observation (New-SyntheticObservation -Mutation 'none')
if ($allPredicates.Count -lt 20) {
    "FAIL the grader answers only $($allPredicates.Count) predicates"
    $failures++
}

# ===========================================================================
# W4-A4 (#234): the MajorUpgrade grader, over the same kind of synthetic
# observations. Written to be what each pair GENUINELY produces, not what would
# make the grader look right.
# ===========================================================================

$FROZEN_UPGRADE_CODE = '0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05'
$A_EXE_SHA = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1'
$B_EXE_SHA = 'b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2'
$SENTINEL_SHA = 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3'
$OTHER_SHA = 'd4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4'
# W4-A5 (#234). Where a package with no remember-property goes when its command
# line says nothing: the resolved default, never the prefix the first install
# was given.
$DEFAULT_LOCATION = 'C:\Program Files\Tesserafin\Server'

function New-SyntheticUpgradeObservation {
    param([Parameter(Mandatory = $true)] [string] $Control)

    # The correct pair is graded against the observation the correct fresh
    # install produces, so the ACL, failure-action and binPath halves of the
    # upgrade grader are exercised with the SAME data the W4-A2 and W4-A3 rows
    # were proven with rather than with a second, looser copy of it.
    $fresh = New-SyntheticObservation -Mutation 'none'

    # `upgrade-no-service` is the pair whose second package registers nothing.
    # The SCM key is gone, so the binPath, the start type, the account and the
    # failure policy are all gone with it -- which is exactly the nineteen-row
    # consequence set the control declares.
    $service = $fresh.service
    $serviceState = 'Stopped'
    $failureActions = $fresh.failureActions
    if ($Control -eq 'upgrade-no-service') {
        $service = $null
        $serviceState = 'Absent'
        $failureActions = $null
    }

    # `upgrade-same-exe` is the pair whose second package was built from the
    # first's stage: what lands on disk IS B's staged executable, and it is also
    # A's, so `exeIsB` stays green and `exeReplaced` is the only row that can go
    # red. An observation that reddened both would be a package that delivered
    # no executable at all, and no B in this slice does that.
    $aStagedExe = $A_EXE_SHA
    $bStagedExe = $B_EXE_SHA
    $installedExe = $B_EXE_SHA
    if ($Control -eq 'upgrade-same-exe') {
        $bStagedExe = $A_EXE_SHA
        $installedExe = $A_EXE_SHA
    }

    # W4-A5. `upgrade-no-remember` is the pair whose second package resolved the
    # DEFAULT directory, so nothing of B is under P and A's payload went with A.
    # The service is registered and its argument list is the §4 one -- the
    # package is correct in every way except where it put itself -- so the
    # binPath is the same string with the prefix replaced, which is what the
    # installed package genuinely produces. The state directories are untouched:
    # DATAFOLDER is not redirected and never was.
    $installedUnderPrefix = ($Control -ne 'upgrade-no-remember')
    $remembered = $(if ($installedUnderPrefix) { $prefix } else { $null })
    if (-not $installedUnderPrefix) {
        $installedExe = $null
        $service = @{} + $fresh.service
        $service.ImagePath = ([string]$fresh.service.ImagePath).Replace($prefix, $DEFAULT_LOCATION)
    }

    # `upgrade-wipes-state` empties the four directories. The directories
    # themselves survive: the retained components are still Permanent, and
    # RemoveFile removes files.
    $state = @{}
    foreach ($name in @('config', 'data', 'cache', 'log')) {
        if ($Control -eq 'upgrade-wipes-state') {
            $state[$name] = @{ directory = $true; sentinel = $false; sha256 = $null }
        } else {
            $state[$name] = @{ directory = $true; sentinel = $true; sha256 = $SENTINEL_SHA }
        }
    }

    $bUpgradeCode = $(if ($Control -eq 'upgrade-upgradecode') {
        '6b1e8d37-5f92-4a04-8e7c-3d05b9f2a618' } else { $FROZEN_UPGRADE_CODE })

    $observation = @{
        aMsi = @{
            productCode = '{11111111-1111-4111-8111-111111111111}'
            productVersion = '1.0.0'
            upgradeCode = "{$($FROZEN_UPGRADE_CODE.ToUpperInvariant())}"
            stagedExeSha256 = $aStagedExe
            startsServiceOnInstall = $false
        }
        bMsi = @{
            productCode = '{22222222-2222-4222-8222-222222222222}'
            productVersion = '1.0.1'
            upgradeCode = "{$($bUpgradeCode.ToUpperInvariant())}"
            stagedExeSha256 = $bStagedExe
            startsServiceOnInstall = ($Control -eq 'upgrade-starts-service')
        }
        upgradeExit = 0
        installPrefix = $prefix
        programDataRoot = $dataRoot
        serverRelativeExe = $serverExe
        webRelativeDir = $webDir
        ffmpegRelativeExe = $ffmpegExe
        installedExeSha256 = $installedExe
        installedServerExe = $installedUnderPrefix
        installedWebDir = $installedUnderPrefix
        installedFfmpegExe = $installedUnderPrefix
        # W4-A5. Every B in this slice is installed with no INSTALLFOLDER on its
        # command line, the relocating control included: that is the sequence
        # under test, not the defect.
        bInstallOmittedInstallFolder = $true
        rememberedInstallFolder = $remembered
        programFilesTesserafinExists = (-not $installedUnderPrefix)
        service = $service
        serviceState = $serviceState
        failureActions = $failureActions
        acls = $fresh.acls
        stateAfterUpgrade = $state
        sentinelSha256 = $SENTINEL_SHA
        aProductInstalled = $false
        bProductInstalled = $true
    }
    return $observation
}

$upgradeControls = @('none') + @((Get-W4UpgradeControlExpectations).Keys)
$upgradeRedSets = @{}

foreach ($control in $upgradeControls) {
    $predicates = Get-W4UpgradePredicates -Observation (New-SyntheticUpgradeObservation -Control $control)
    $verdict = Get-W4UpgradeVerdict -Predicates $predicates -Control $control
    $status = if ($verdict.passed) { 'OK  ' } else { 'FAIL' }
    "$status upgrade/$($control.PadRight(24)) $($verdict.detail)"
    if (-not $verdict.passed) { $failures++ }
    $upgradeRedSets[($verdict.red -join '|')] = $true
}

if ($upgradeRedSets.Count -ne $upgradeControls.Count) {
    "FAIL distinct upgrade red sets: $($upgradeRedSets.Count) across $($upgradeControls.Count) controls"
    $failures++
}

$upgradePredicates = Get-W4UpgradePredicates -Observation (New-SyntheticUpgradeObservation -Control 'none')
if ($upgradePredicates.Count -lt 20) {
    "FAIL the upgrade grader answers only $($upgradePredicates.Count) predicates"
    $failures++
}

# `productCodesDiffer` is the row a careless grader gets wrong in the direction
# no control above can catch: written as "not equal" alone it grades two
# packages that carry NO ProductCode at all as a valid upgrade pair, because an
# absent value is not equal to anything -- including another absent one.
$blindProductCodes = New-SyntheticUpgradeObservation -Control 'none'
$blindProductCodes.aMsi = @{} + $blindProductCodes.aMsi
$blindProductCodes.bMsi = @{} + $blindProductCodes.bMsi
$blindProductCodes.aMsi.Remove('productCode')
$blindProductCodes.bMsi.Remove('productCode')
if ((Get-W4UpgradePredicates -Observation $blindProductCodes)['productCodesDiffer']) {
    'FAIL productCodesDiffer is green for two packages that carry no ProductCode'
    $failures++
}

# W4-A5. `rememberedPrefixIsInstallPrefix` is the row a careless grader gets
# wrong in the direction no control catches: a comparison that accepted an
# absent value, or any value, would call a package that remembers the WRONG
# prefix correct -- and the next upgrade would then relocate exactly as NB-3
# describes. The absent case is covered by `upgrade-no-remember`; a remembered
# prefix that is simply a different directory is not, so it is asserted here.
$strayPrefix = New-SyntheticUpgradeObservation -Control 'none'
$strayPrefix.rememberedInstallFolder = 'D:\a\_temp\w4a0\somewhere-else'
if ((Get-W4UpgradePredicates -Observation $strayPrefix)['rememberedPrefixIsInstallPrefix']) {
    'FAIL rememberedPrefixIsInstallPrefix is green for a remembered prefix that is not the one used'
    $failures++
}
# ...and the proof-shape row: a run that put INSTALLFOLDER back on B's command
# line is measuring the W4-A4 sequence, whatever else it grades green.
$toldAgain = New-SyntheticUpgradeObservation -Control 'none'
$toldAgain.bInstallOmittedInstallFolder = $false
if ((Get-W4UpgradePredicates -Observation $toldAgain)['upgradeOmittedInstallFolder']) {
    'FAIL upgradeOmittedInstallFolder is green for a B that was told the prefix again'
    $failures++
}

# The two digest predicates are the ones a careless grader gets wrong in the
# direction that CANNOT be caught by any control above: a comparison that treats
# an absent digest as a match would call an upgrade that delivered nothing a
# success. Neither control produces that observation, so it is asserted here.
foreach ($case in @(
    @{ what = 'a missing installed digest'; installed = $null }
    @{ what = 'an empty installed digest'; installed = '' })) {
    $blind = New-SyntheticUpgradeObservation -Control 'none'
    $blind.installedExeSha256 = $case.installed
    $blindPredicates = Get-W4UpgradePredicates -Observation $blind
    if ($blindPredicates['exeIsB']) {
        "FAIL exeIsB is green for $($case.what)"
        $failures++
    }
}
# ...and the mirror image: an installed executable that is neither package's is
# still not B's, however different it is from A's.
$strayObservation = New-SyntheticUpgradeObservation -Control 'none'
$strayObservation.installedExeSha256 = $OTHER_SHA
$strayPredicates = Get-W4UpgradePredicates -Observation $strayObservation
if ($strayPredicates['exeIsB'] -or (-not $strayPredicates['exeReplaced'])) {
    'FAIL a third executable does not grade as replaced-but-not-B'
    $failures++
}

if ($failures -gt 0) {
    "W4 assertion self-test FAILED with $failures problem(s)"
    exit 1
}
"W4 assertion self-test: $($mutations.Count) fresh-install mutations, $($allPredicates.Count) predicates; " +
    "$($upgradeControls.Count) upgrade controls, $($upgradePredicates.Count) predicates; all as declared"
exit 0
