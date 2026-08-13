<#
.SYNOPSIS
    Install a locked MSYS2 package set from local files only (#236, W1-R).

.DESCRIPTION
    The validation-path installer. It takes a bundle that is already on disk —
    it does not pull from a registry, because W1-R-A publishes nothing — and
    installs every locked archive with `pacman -U`.

    Before installing, EVERY MSYS2 mirror is emptied. That is the proof, not
    tidiness: if the locked set were incomplete or anything still resolved
    dynamically, pacman would need a repository and would fail. A successful
    install with no mirror configured is evidence that nothing upstream was
    consulted.

    `consume.ps1` is the digest-pinned GHCR consumer a future W1-A2 calls; this
    script is the same install and verification without the registry step.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundleDir,
    [Parameter(Mandatory = $true)][string]$EvidenceDir,
    [string]$MsysRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. (Join-Path (Split-Path -Parent $PSCommandPath) 'common.ps1')

function Stop-Hard {
    param([string]$Message)
    throw "W1-R INSTALL HARD STOP: $Message"
}

New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

# Prefer an explicitly supplied root. Deriving one from `bash.exe` on PATH
# finds Git for Windows' bash on a hosted runner, and that tree has no
# etc/pacman.d — the caller must say which MSYS2 this is.
if (-not $MsysRoot) {
    $bashCommand = Get-Command bash.exe -ErrorAction SilentlyContinue
    if (-not $bashCommand) { Stop-Hard 'no bash.exe on PATH and no -MsysRoot given' }
    $MsysRoot = Split-Path -Parent (Split-Path -Parent $bashCommand.Source)
    Write-Host "no -MsysRoot given; derived $MsysRoot from $($bashCommand.Source)"
}
$MsysRoot = $MsysRoot.TrimEnd('\', '/')
$bash = Join-Path $MsysRoot 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash)) { Stop-Hard "no bash at $bash" }
$pacman = Join-Path $MsysRoot 'usr\bin\pacman.exe'
if (-not (Test-Path -LiteralPath $pacman)) {
    Stop-Hard "no pacman at $pacman; '$MsysRoot' is not an MSYS2 installation"
}
Write-Host "MSYS2 root: $MsysRoot"

# ── 1. The bundle must match its own manifest before anything is installed ──
$recorded = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $BundleDir 'manifest.sha256')) {
    if ($line -match '^(?<sha>[0-9a-f]{64})\s\s(?<path>.+)$') {
        $recorded[$Matches['path']] = $Matches['sha']
    }
}
if ($recorded.Count -eq 0) { Stop-Hard 'the bundle carries no manifest.sha256 entries' }

$actualPaths = Get-ChildItem -LiteralPath $BundleDir -Recurse -File |
    ForEach-Object { $_.FullName.Substring($BundleDir.Length + 1).Replace('\', '/') } |
    Where-Object { $_ -ne 'manifest.sha256' }

$missing = @($recorded.Keys | Where-Object { $actualPaths -notcontains $_ })
$extra = @($actualPaths | Where-Object { -not $recorded.ContainsKey($_) })
if ($missing.Count -gt 0) { Stop-Hard "missing bundle path(s): $($missing -join ', ')" }
if ($extra.Count -gt 0) { Stop-Hard "undeclared bundle path(s): $($extra -join ', ')" }

foreach ($path in $recorded.Keys) {
    $full = Join-Path $BundleDir ($path -replace '/', '\')
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $recorded[$path]) {
        Stop-Hard "$path hashes to $hash, the bundle records $($recorded[$path])"
    }
}
Write-Host "bundle verified: $($recorded.Count) paths match manifest.sha256"

$lockPath = Join-Path $BundleDir 'msys2-lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$lockSha = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()

$archives = @(Get-ChildItem -LiteralPath (Join-Path $BundleDir 'packages') -Filter '*.pkg.tar.zst' -File)
if ($archives.Count -ne $lock.packageCount) {
    Stop-Hard "$($archives.Count) archives present, the lock declares $($lock.packageCount)"
}

# ── 2. Remove every mirror. This is what makes the next step evidence. ──────
$mirrorDir = Join-Path $MsysRoot 'etc\pacman.d'
$emptied = @()
if (Test-Path -LiteralPath $mirrorDir) {
    foreach ($file in Get-ChildItem -LiteralPath $mirrorDir -Filter 'mirrorlist*' -File) {
        $emptied += $file.Name
        Set-Content -LiteralPath $file.FullName -Encoding utf8NoBOM -Value @(
            '# Emptied by ci/windows/build-inputs/install-locked.ps1 (#236, W1-R).',
            '# W1 installs from locally verified package files only.'
        )
    }
}
if ($emptied.Count -eq 0) { Stop-Hard 'no mirrorlist was found to empty; the no-upstream proof would be vacuous' }
Write-Host "mirrors emptied: $($emptied -join ', ')"

# `name|version|architecture` for every installed package. The exact-set ruling
# is made by installed-set.py so that it can be exercised against synthetic
# observations on Linux; this shell only makes the observation.
$queryInstalled = @'
LC_ALL=C pacman -Qi | awk -F': ' '/^Name /{n=$2} /^Version /{v=$2} /^Architecture /{print n"|"v"|"$2}'
'@

$before = @(& $bash -lc $queryInstalled | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { Stop-Hard "pacman -Qi exited $LASTEXITCODE" }
Set-Content -LiteralPath (Join-Path $EvidenceDir 'installed-before.txt') -Encoding utf8NoBOM -Value $before

# ── 3. Install. `-U` over explicit local files; no `-S`, no `-Syu`. ─────────
#
# In TWO phases, and the reason is measured rather than defensive. The runner's
# MSYS2 carries an older msys2-runtime than the lock, so a single transaction
# replaces msys-2.0.dll underneath the very pacman that is running on it, and
# every subsequent post-install script dies with
#
#     error: could not fork a new process (Resource temporarily unavailable)
#
# That is what the second hosted run recorded. MSYS2's own core update has the
# same shape and the same remedy: update the runtime, leave the shell, come
# back in a new one. Each `& $bash -lc` is a new process, so phase 2 already
# runs on the replaced runtime.
#
# This is still `pacman -U` over local files only. No `-S`, no `-Syu`, no
# mirror: the phases change WHEN packages are installed, never WHERE they come
# from.
$posix = (& $bash -lc "cygpath -u '$($BundleDir -replace '\\', '/')'").Trim()
if ($LASTEXITCODE -ne 0) { Stop-Hard "cygpath exited $LASTEXITCODE" }

# Phase 1 carries NO `--needed`. With it, a runner whose MSYS2 already holds the
# locked runtime prints
#
#     warning: msys2-runtime-3.6.10-2 is up to date -- skipping
#
# and the transaction the phase exists to perform never happens. The phase would
# then be exercised only by the accident of an outdated runner image, and would
# rot silently the day the image caught up. Reinstalling the locked archive
# unconditionally makes the replacement path run on EVERY run, which is the only
# way it stays proven.
$lockedRuntime = @($lock.packages | Where-Object { $_.name -eq 'msys2-runtime' })
$runtime = @($archives | Where-Object { $_.Name -like 'msys2-runtime-*' })
if ($runtime.Count -gt 1) {
    Stop-Hard "the lock declares $($runtime.Count) msys2-runtime archives"
}
if ($runtime.Count -ne $lockedRuntime.Count) {
    Stop-Hard "the lock names $($lockedRuntime.Count) msys2-runtime package(s) but $($runtime.Count) archive(s) are present"
}

$runtimeProof = [ordered]@{ performed = $false }

if ($runtime.Count -eq 1) {
    $runtimeVersion = $lockedRuntime[0].version
    $stdoutFile = Join-Path $EvidenceDir 'pacman-stdout.log'
    $stderrFile = Join-Path $EvidenceDir 'pacman-stderr.log'

    # Note the absent `--needed`.
    $first = "echo w1r-phase1-pid=`$`$; pacman -U --noconfirm --overwrite '*' $posix/packages/$($runtime[0].Name)"
    Write-Host "phase 1 (core runtime, forced reinstall): $first"
    & $bash -lc $first > $stdoutFile 2> $stderrFile
    $runtimeExit = $LASTEXITCODE
    $phase1 = @(Get-Content -LiteralPath $stdoutFile -ErrorAction SilentlyContinue) +
              @(Get-Content -LiteralPath $stderrFile -ErrorAction SilentlyContinue)
    $phase1 | Write-Host
    if ($runtimeExit -ne 0) { Stop-Hard "phase 1 pacman -U exited $runtimeExit" }

    # pacman's own account of what it did. `skipping` is the failure this phase
    # was rewritten to make impossible, so it is refused by name rather than
    # merely not matched.
    if ($phase1 -match 'skipping msys2-runtime') {
        Stop-Hard 'phase 1 skipped the runtime; the replacement path did not run'
    }
    $applied = @($phase1 | Where-Object { $_ -match '(reinstalling|upgrading|downgrading|installing)\s+msys2-runtime' })
    if ($applied.Count -eq 0) {
        Stop-Hard 'phase 1 produced no msys2-runtime transaction line; pacman did not touch the runtime'
    }
    $phase1Pid = $phase1 |
        ForEach-Object { if ($_ -match '^w1r-phase1-pid=(\d+)$') { $Matches[1] } } |
        Select-Object -First 1
    if (-not $phase1Pid) { Stop-Hard 'phase 1 did not report its shell pid' }

    # That shell is now gone: the process exited with `& $bash -lc`, and the
    # msys-2.0.dll it mapped went with it. Every later `& $bash -lc` is a new
    # process, which is what MSYS2's own core update asks for.
    Start-Sleep -Seconds 5

    # A FRESH process, before phase 2 installs anything, reporting the runtime it
    # is itself running on.
    $probe = @(& $bash -lc 'echo w1r-phase2-pid=$$; uname -r; LC_ALL=C pacman -Q msys2-runtime' |
        Where-Object { $_ })
    if ($LASTEXITCODE -ne 0) { Stop-Hard "the phase 2 runtime probe exited $LASTEXITCODE" }
    $probe | Write-Host
    $phase2Pid = $probe |
        ForEach-Object { if ($_ -match '^w1r-phase2-pid=(\d+)$') { $Matches[1] } } |
        Select-Object -First 1
    if (-not $phase2Pid) { Stop-Hard 'the phase 2 probe did not report its shell pid' }
    if ($phase2Pid -eq $phase1Pid) {
        Stop-Hard "phase 2 reused the phase 1 shell (pid $phase1Pid); the runtime would not have been replaced under it"
    }

    $unameRelease = [string]($probe | Where-Object { $_ -match '^\d+\.' } | Select-Object -First 1)
    if (-not $unameRelease) { Stop-Hard 'the phase 2 probe reported no `uname -r`' }
    if (-not $unameRelease.StartsWith($runtimeVersion)) {
        Stop-Hard "phase 2 runs on MSYS2 runtime '$unameRelease', the lock names $runtimeVersion"
    }
    $queried = [string]($probe | Where-Object { $_ -match '^msys2-runtime\s' } | Select-Object -First 1)
    if ($queried -ne "msys2-runtime $runtimeVersion") {
        Stop-Hard "after phase 1 pacman reports '$queried', the lock names msys2-runtime $runtimeVersion"
    }

    $runtimeProof = [ordered]@{
        performed          = $true
        needed             = $false
        lockedVersion      = $runtimeVersion
        archive            = $runtime[0].Name
        transactionLines   = $applied
        phase1ShellPid     = $phase1Pid
        phase2ShellPid     = $phase2Pid
        freshProcess       = $true
        phase2UnameRelease = $unameRelease
        phase2PacmanQuery  = $queried
    }
}

$stderrFile2 = Join-Path $EvidenceDir 'pacman-stderr-phase2.log'
$command = "pacman -U --noconfirm --needed --overwrite '*' $posix/packages/*.pkg.tar.zst"
Write-Host "phase 2 (everything else): $command"
& $bash -lc $command 2> $stderrFile2
$installExit = $LASTEXITCODE
Get-Content -LiteralPath $stderrFile2 -ErrorAction SilentlyContinue | Write-Host
if ($installExit -ne 0) { Stop-Hard "phase 2 pacman -U exited $installExit" }

# The runtime package and the files it owns, against the LOCKED ARCHIVE rather
# than against pacman's own database — `pacman -Qkk` would only prove pacman
# agrees with itself. The file list pacman records must equal the archive's
# members, and the runtime the prefix is now running on must be the very bytes
# the lock names.
if ($runtime.Count -eq 1) {
    $ownedScript = @"
set -euo pipefail
archive='$posix/packages/$($runtime[0].Name)'
tmp=`$(mktemp -d)
trap 'rm -rf "`$tmp"' EXIT
bsdtar -C "`$tmp" -xf "`$archive" usr/bin/msys-2.0.dll
packaged=`$(sha256sum "`$tmp/usr/bin/msys-2.0.dll" | cut -d' ' -f1)
installed=`$(sha256sum /usr/bin/msys-2.0.dll | cut -d' ' -f1)
if [ "`$packaged" != "`$installed" ]; then
  echo "the installed msys-2.0.dll (`$installed) is not the locked one (`$packaged)" >&2
  exit 1
fi
LC_ALL=C pacman -Ql msys2-runtime | awk '{print substr(`$0, index(`$0, " ") + 1)}' |
  grep -v '/`$' | sed 's|^/||' | LC_ALL=C sort > "`$tmp/owned"
bsdtar tf "`$archive" | grep -v '^\.' | grep -v '/`$' | LC_ALL=C sort > "`$tmp/members"
diff "`$tmp/owned" "`$tmp/members" >&2
echo "w1r-runtime-dll-sha256=`$packaged"
echo "w1r-runtime-owned-files=`$(wc -l < "`$tmp/owned")"
"@
    $owned = @(& $bash -lc $ownedScript | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0) {
        Stop-Hard "the installed msys2-runtime does not match the locked archive (exit $LASTEXITCODE)"
    }
    $owned | Write-Host
    $runtimeProof['dllSha256'] = [string]($owned |
        ForEach-Object { if ($_ -match '^w1r-runtime-dll-sha256=(.+)$') { $Matches[1] } } |
        Select-Object -First 1)
    $runtimeProof['ownedFiles'] = [int]($owned |
        ForEach-Object { if ($_ -match '^w1r-runtime-owned-files=(\d+)$') { $Matches[1] } } |
        Select-Object -First 1)
    $runtimeProof['ownedFilesMatchArchive'] = $true
}

# ── 4. The prefix must now hold EXACTLY the locked set ─────────────────────
#
# Not "everything locked is present", which a prefix carrying an extra compiler
# also satisfies. Exact equality of name, version and architecture: an
# undeclared package could influence the FFmpeg build while appearing in no
# provenance, and a future runner image that gains one must fail here.
$after = @(& $bash -lc $queryInstalled | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { Stop-Hard "pacman -Qi exited $LASTEXITCODE" }
$observedFile = Join-Path $EvidenceDir 'installed-after.txt'
Set-Content -LiteralPath $observedFile -Encoding utf8NoBOM -Value $after

$setEvidence = Join-Path $EvidenceDir 'installed-set.json'
$scriptDir = Split-Path -Parent $PSCommandPath
$python = Get-PythonPath
& $python (Join-Path $scriptDir 'installed-set.py') `
    --lock $lockPath --observed $observedFile --json $setEvidence
if ($LASTEXITCODE -ne 0) { Stop-Hard "the installed set is not equal to the lock (exit $LASTEXITCODE)" }

# Recorded, now that equality is proven: which of the locked packages the runner
# image already carried at the locked version before this script ran.
$lockedTriples = @($lock.packages | ForEach-Object { "$($_.name)|$($_.version)|$($_.architecture)" })
$preexisting = @($before | Where-Object { $lockedTriples -contains $_ })
$replaced = @($before | Where-Object { $lockedTriples -notcontains $_ })

$evidence = [ordered]@{
    probe             = 'w1r-install-locked'
    msysRoot          = $MsysRoot
    lockSha256        = $lockSha
    packageCount      = $lock.packageCount
    archivesPresent   = $archives.Count
    verifiedPaths     = $recorded.Count
    installedBefore   = $before.Count
    installedAfter    = $after.Count
    installedSetEqualsLock = $true
    preexistingAtLockedVersion = $preexisting.Count
    preexistingNotAtLockedVersion = $replaced.Count
    runtimeReinstall  = $runtimeProof
    mirrorsEmptied    = $emptied
    upstreamConsulted = $false
    pacmanMode        = 'pacman -U over local files only; no -S, no -Syu, no mirror configured'
    installPhases     = if ($runtime.Count -eq 1) { 'core runtime reinstalled first, then the rest in a new shell' } else { 'single transaction' }
}
$evidence | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $EvidenceDir 'install-locked.json') -Encoding utf8NoBOM

Write-Host "installed all $($lock.packageCount) locked packages with no mirror configured"
