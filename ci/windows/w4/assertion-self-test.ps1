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
    # `installFolder` is deliberately NOT given Administrators, SYSTEM or Users
    # rows. Under the disposable prefix those are inherited from the runner's
    # temp tree and not from %ProgramFiles%, the probe records them as evidence
    # and no predicate grades them, so inventing them here would be inventing an
    # answer to a question nothing asks.
    $serviceSid = 'S-1-5-80-761762137-1691453069-3789821951-3290391601-3361247659'
    $installFolderMask = $(if ($Mutation -eq 'acl-install-writable') { 0x1301BF } else { 0x1200A9 })

    $dataRootRules = [System.Collections.Generic.List[object]]::new()
    $null = $dataRootRules.Add([ordered]@{ sid = 'S-1-5-32-544'; rights = 0x1F01FF; type = 'Allow'; inherited = $false })
    $null = $dataRootRules.Add([ordered]@{ sid = 'S-1-5-18';     rights = 0x1F01FF; type = 'Allow'; inherited = $false })
    if ($Mutation -ne 'acl-no-service-grant') {
        $null = $dataRootRules.Add([ordered]@{ sid = $serviceSid; rights = 0x1301BF; type = 'Allow'; inherited = $false })
    }
    if ($Mutation -eq 'acl-not-protected' -or $Mutation -eq 'acl-users-write') {
        $null = $dataRootRules.Add([ordered]@{ sid = 'S-1-5-32-545'; rights = 0x1301BF; type = 'Allow'; inherited = $false })
    }
    $dataRootProtected = ($Mutation -ne 'acl-not-protected')

    # The four state directories carry the data root's ACEs by inheritance --
    # every ACE the authoring states is OICI -- so they are the same rows with
    # `inherited` set, and their own protection flag is $false, which is what a
    # directory that inherits looks like and is not what `dataRootInheritanceBroken`
    # asks about.
    $inheritedRules = @(foreach ($rule in $dataRootRules) {
        [ordered]@{ sid = $rule.sid; rights = $rule.rights; type = $rule.type; inherited = $true }
    })

    $acls = [ordered]@{
        installFolder = [ordered]@{
            path = $prefix
            protected = $false
            rules = @([ordered]@{ sid = $serviceSid; rights = $installFolderMask; type = 'Allow'; inherited = $false })
        }
        dataRoot = [ordered]@{
            path = 'C:\ProgramData\Tesserafin'
            protected = $dataRootProtected
            rules = @($dataRootRules)
        }
    }
    foreach ($name in @('config', 'data', 'cache', 'log')) {
        $acls[$name] = [ordered]@{
            path = (Join-W4Path -Root $dataRoot -Relative $name)
            protected = $false
            rules = $inheritedRules
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

if ($failures -gt 0) {
    "W4-A0 assertion self-test FAILED with $failures problem(s)"
    exit 1
}
"W4-A0 assertion self-test: $($mutations.Count) mutations, $($allPredicates.Count) predicates, all as declared"
exit 0
