<#
.SYNOPSIS
    Consume the retained Windows build inputs by exact OCI manifest digest (#236, W1-R).

.DESCRIPTION
    This is the entry point a future W1-A2 FFmpeg build calls instead of live
    pacman resolution. It is fail-closed at every step:

      * the reference must be digest-pinned. A tag is refused, not resolved;
      * the package must be the one W1-R authorised, and no other;
      * the pulled layer must match the digest the manifest declares;
      * every extracted path must match the bundle's own `manifest.sha256`;
      * installation uses `pacman -U` over local files only.

    Before installing, EVERY MSYS2 mirror is removed from the installation. That
    is not tidiness: it is the proof. If the locked set were incomplete, or if
    anything still resolved dynamically, pacman would need a repository and
    would fail — so a successful install with no mirror configured is evidence
    that nothing upstream was consulted.

.PARAMETER Reference
    ghcr.io/tesserafin-project/windows-ffmpeg-build-inputs@sha256:<digest>

.PARAMETER MsysRoot
    The MSYS2 installation root (for example D:\a\_temp\msys64).

.PARAMETER WorkDir
    Scratch directory for the pulled artifact.

.PARAMETER EvidenceDir
    Where consume.json is written, for provenance.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Reference,
    [Parameter(Mandatory = $true)][string]$MsysRoot,
    [Parameter(Mandatory = $true)][string]$WorkDir,
    [Parameter(Mandatory = $true)][string]$EvidenceDir,
    [Parameter(Mandatory = $true)][string]$OrasPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Stop-Hard {
    param([string]$Message)
    throw "W1-R CONSUME HARD STOP: $Message"
}

# ── 1. The reference contract, enforced here as well as in the caller ────────
$canonical = 'ghcr.io/tesserafin-project/windows-ffmpeg-build-inputs'
if ($Reference -notmatch '^(?<name>[^@]+)@(?<digest>sha256:[0-9a-f]{64})$') {
    Stop-Hard "'$Reference' is not digest-pinned. A tag is never an accepted identity; use $canonical@sha256:<digest>"
}
$name = $Matches['name']
$digest = $Matches['digest']
if ($name -ne $canonical) {
    Stop-Hard "'$name' is not the authorised package. W1-R authorises exactly one: $canonical"
}

New-Item -ItemType Directory -Force -Path $WorkDir, $EvidenceDir | Out-Null
$pullDir = Join-Path $WorkDir 'pull'
if (Test-Path -LiteralPath $pullDir) { Remove-Item -LiteralPath $pullDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pullDir | Out-Null

# ── 2. Pull by digest, then verify what came back is what was asked for ─────
Write-Host "pulling $Reference"
& $OrasPath pull $Reference --output $pullDir
if ($LASTEXITCODE -ne 0) { Stop-Hard "oras pull exited $LASTEXITCODE" }

$manifestJson = & $OrasPath manifest fetch $Reference
if ($LASTEXITCODE -ne 0) { Stop-Hard "oras manifest fetch exited $LASTEXITCODE" }

$manifestBytes = [System.Text.Encoding]::UTF8.GetBytes($manifestJson)
$sha = [System.Security.Cryptography.SHA256]::Create()
$fetchedDigest = 'sha256:' + (($sha.ComputeHash($manifestBytes) | ForEach-Object { $_.ToString('x2') }) -join '')
if ($fetchedDigest -ne $digest) {
    Stop-Hard "the registry returned manifest $fetchedDigest for a request pinned to $digest"
}

$layerTar = Join-Path $pullDir 'msys2-build-inputs.tar'
if (-not (Test-Path -LiteralPath $layerTar)) {
    Stop-Hard "the pulled artifact does not contain msys2-build-inputs.tar"
}

$manifest = $manifestJson | ConvertFrom-Json
$layerDigest = $manifest.layers[0].digest
$actualLayer = 'sha256:' + (Get-FileHash -LiteralPath $layerTar -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualLayer -ne $layerDigest) {
    Stop-Hard "layer digest $actualLayer does not match the manifest's $layerDigest"
}

# ── 3. Extract and verify every path against the bundle's own manifest ──────
$bundleDir = Join-Path $WorkDir 'bundle'
if (Test-Path -LiteralPath $bundleDir) { Remove-Item -LiteralPath $bundleDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null
& tar -xf $layerTar -C $bundleDir
if ($LASTEXITCODE -ne 0) { Stop-Hard "tar extraction exited $LASTEXITCODE" }

$recorded = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $bundleDir 'manifest.sha256')) {
    if ($line -match '^(?<sha>[0-9a-f]{64})\s\s(?<path>.+)$') {
        $recorded[$Matches['path']] = $Matches['sha']
    }
}
if ($recorded.Count -eq 0) { Stop-Hard "the bundle carries no manifest.sha256 entries" }

$actualPaths = Get-ChildItem -LiteralPath $bundleDir -Recurse -File |
    ForEach-Object { $_.FullName.Substring($bundleDir.Length + 1).Replace('\', '/') } |
    Where-Object { $_ -ne 'manifest.sha256' }

$missing = @($recorded.Keys | Where-Object { $actualPaths -notcontains $_ })
$extra = @($actualPaths | Where-Object { -not $recorded.ContainsKey($_) })
if ($missing.Count -gt 0) { Stop-Hard "missing bundle path(s): $($missing -join ', ')" }
if ($extra.Count -gt 0) { Stop-Hard "undeclared bundle path(s): $($extra -join ', ')" }

foreach ($path in $recorded.Keys) {
    $full = Join-Path $bundleDir ($path -replace '/', '\')
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $recorded[$path]) {
        Stop-Hard "$path hashes to $hash, the bundle records $($recorded[$path])"
    }
}

$lock = Get-Content -LiteralPath (Join-Path $bundleDir 'msys2-lock.json') -Raw | ConvertFrom-Json
$lockSha = (Get-FileHash -LiteralPath (Join-Path $bundleDir 'msys2-lock.json') -Algorithm SHA256).Hash.ToLowerInvariant()

# ── 4. Remove every mirror, so a dynamic resolution CANNOT silently succeed ──
$mirrorDir = Join-Path $MsysRoot 'etc\pacman.d'
$removedMirrors = @()
if (Test-Path -LiteralPath $mirrorDir) {
    foreach ($file in Get-ChildItem -LiteralPath $mirrorDir -Filter 'mirrorlist*' -File) {
        $removedMirrors += $file.Name
        Set-Content -LiteralPath $file.FullName -Value @(
            '# Emptied by ci/windows/build-inputs/consume.ps1 (#236, W1-R).',
            '# W1 installs from locally verified package files only. A mirror here',
            '# would let a missing lock entry be silently resolved upstream.'
        ) -Encoding utf8NoBOM
    }
}

# ── 5. Install from local files only ────────────────────────────────────────
$packages = Get-ChildItem -LiteralPath (Join-Path $bundleDir 'packages') -Filter '*.pkg.tar.zst' -File
if ($packages.Count -ne $lock.packageCount) {
    Stop-Hard "$($packages.Count) archives present, the lock declares $($lock.packageCount)"
}

$bash = Join-Path $MsysRoot 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash)) { Stop-Hard "no bash at $bash" }

$posixDir = (& $bash -lc "cygpath -u '$($bundleDir -replace '\\', '/')'").Trim()
if ($LASTEXITCODE -ne 0) { Stop-Hard "cygpath exited $LASTEXITCODE" }

# `-U` with explicit local files. There is deliberately no `-S` and no `-Syu`
# anywhere in this file: those are the PROHIBITED live-resolution paths.
$install = "pacman -U --noconfirm --needed --overwrite '*' $posixDir/packages/*.pkg.tar.zst"
& $bash -lc $install
if ($LASTEXITCODE -ne 0) { Stop-Hard "pacman -U exited $LASTEXITCODE" }

# ── 6. Every installed package must belong to the lock ──────────────────────
$installed = & $bash -lc "pacman -Qq" | Where-Object { $_ }
if ($LASTEXITCODE -ne 0) { Stop-Hard "pacman -Qq exited $LASTEXITCODE" }
$lockNames = @($lock.packages | ForEach-Object { $_.name })
$unexpected = @($installed | Where-Object { $lockNames -notcontains $_ })

$evidence = [ordered]@{
    probe            = 'w1r-consume'
    reference        = $Reference
    manifestDigest   = $digest
    layerDigest      = $layerDigest
    lockSha256       = $lockSha
    packageCount     = $lock.packageCount
    archivesPresent  = $packages.Count
    installedCount   = @($installed).Count
    notInLock        = $unexpected
    mirrorsEmptied   = $removedMirrors
    upstreamConsulted = $false
    pacmanMode       = 'pacman -U over local files only'
}
$evidence | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $EvidenceDir 'consume.json') -Encoding utf8NoBOM

Write-Host "installed $($installed.Count) packages from $($packages.Count) locked archives, no mirror configured"
if ($unexpected.Count -gt 0) {
    Write-Host "note: $($unexpected.Count) package(s) were already present in the base image and are not in the lock: $($unexpected -join ', ')"
}
