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

function Stop-Hard {
    param([string]$Message)
    throw "W1-R INSTALL HARD STOP: $Message"
}

New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

if (-not $MsysRoot) {
    $bashCommand = Get-Command bash.exe -ErrorAction SilentlyContinue
    if (-not $bashCommand) { Stop-Hard 'no bash.exe on PATH; is MSYS2 set up?' }
    $MsysRoot = Split-Path -Parent (Split-Path -Parent $bashCommand.Source)
}
$bash = Join-Path $MsysRoot 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash)) { Stop-Hard "no bash at $bash" }
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

$before = @(& $bash -lc 'pacman -Qq' | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { Stop-Hard "pacman -Qq exited $LASTEXITCODE" }

# ── 3. Install. `-U` over explicit local files; no `-S`, no `-Syu`. ─────────
$posix = (& $bash -lc "cygpath -u '$($BundleDir -replace '\\', '/')'").Trim()
if ($LASTEXITCODE -ne 0) { Stop-Hard "cygpath exited $LASTEXITCODE" }

$stderrFile = Join-Path $EvidenceDir 'pacman-stderr.log'
$command = "pacman -U --noconfirm --needed --overwrite '*' $posix/packages/*.pkg.tar.zst"
Write-Host "installing: $command"
& $bash -lc $command 2> $stderrFile
$installExit = $LASTEXITCODE
Get-Content -LiteralPath $stderrFile -ErrorAction SilentlyContinue | Write-Host
if ($installExit -ne 0) { Stop-Hard "pacman -U exited $installExit" }

# ── 4. Everything the lock names must now be installed ─────────────────────
$after = @(& $bash -lc 'pacman -Qq' | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { Stop-Hard "pacman -Qq exited $LASTEXITCODE" }

$lockNames = @($lock.packages | ForEach-Object { $_.name })
$notInstalled = @($lockNames | Where-Object { $after -notcontains $_ })
if ($notInstalled.Count -gt 0) {
    Stop-Hard "locked package(s) absent after install: $($notInstalled -join ', ')"
}

# Packages present that the lock does not name are the base image's own, not a
# dynamic resolution: no mirror was configured, so nothing could have been
# fetched. They are recorded rather than treated as a failure.
$preexisting = @($before | Where-Object { $lockNames -notcontains $_ })
$unexpected = @($after | Where-Object { $lockNames -notcontains $_ -and $before -notcontains $_ })
if ($unexpected.Count -gt 0) {
    Stop-Hard "package(s) appeared that are neither locked nor pre-existing: $($unexpected -join ', ')"
}

$evidence = [ordered]@{
    probe             = 'w1r-install-locked'
    msysRoot          = $MsysRoot
    lockSha256        = $lockSha
    packageCount      = $lock.packageCount
    archivesPresent   = $archives.Count
    verifiedPaths     = $recorded.Count
    installedBefore   = $before.Count
    installedAfter    = $after.Count
    lockedAllPresent  = $true
    preexistingCount  = $preexisting.Count
    unexpectedCount   = $unexpected.Count
    mirrorsEmptied    = $emptied
    upstreamConsulted = $false
    pacmanMode        = 'pacman -U over local files only; no -S, no -Syu, no mirror configured'
}
$evidence | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $EvidenceDir 'install-locked.json') -Encoding utf8NoBOM

Write-Host "installed all $($lock.packageCount) locked packages with no mirror configured"
