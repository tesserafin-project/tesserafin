<#
.SYNOPSIS
    The handful of things both Windows entry points need (#236, W1-R).

.DESCRIPTION
    `install-locked.ps1` (the validation path) and `consume.ps1` (the
    digest-pinned GHCR path) must not drift apart: whatever the pull adds, the
    installation and its rulings have to be the same code, or the gate proven on
    a pull request is not the gate a W1-A2 build actually runs.
#>

function Get-PythonPath {
    <#
        `python3` is the name on the runner's PATH inside bash; PowerShell
        usually sees `python`. Both are tried rather than assumed, because
        guessing wrong would surface as a missing interpreter rather than as
        the verification result the caller asked for.
    #>
    foreach ($candidate in @('python3', 'python')) {
        $found = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($found) { return $found.Source }
    }
    throw 'W1-R HARD STOP: no python interpreter on PATH'
}
