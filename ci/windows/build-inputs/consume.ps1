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
      * every package signature must verify against the trust root that
        travelled inside the layer, not against the runner's own keyring;
      * installation uses `pacman -U` over local files only, through the very
        script the pull request gates, so the consumer and the proof cannot
        drift apart;
      * the prefix must end up holding exactly the locked package set.

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

. (Join-Path (Split-Path -Parent $PSCommandPath) 'common.ps1')

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

$lockPath = Join-Path $bundleDir 'msys2-lock.json'
if (-not (Test-Path -LiteralPath $lockPath)) { Stop-Hard 'the pulled bundle carries no msys2-lock.json' }
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$lockSha = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()

$scriptDir = Split-Path -Parent $PSCommandPath
$python = Get-PythonPath

# ── 4. Authenticate every signature against the trust root that TRAVELLED ────
#
# The bundle carries `trust/`, the layer digest is pinned by the manifest, and
# the manifest digest is pinned by the reference — so the keys checked here are
# the reviewed committed ones, reached without trusting the runner's ambient
# GnuPG keyring or any keyserver. Attribution is decided before a single archive
# is handed to pacman.
$signatureEvidence = Join-Path $EvidenceDir 'consume-signatures.json'
& $python (Join-Path $scriptDir 'signing.py') `
    --bundle $bundleDir --trust (Join-Path $bundleDir 'trust') |
    Tee-Object -FilePath $signatureEvidence
if ($LASTEXITCODE -ne 0) { Stop-Hard "signature verification exited $LASTEXITCODE" }
$signatures = Get-Content -LiteralPath $signatureEvidence -Raw | ConvertFrom-Json

# ── 5. Install through the SAME path the pull request proves ─────────────────
#
# Not a second implementation. `install-locked.ps1` empties every mirror,
# verifies the bundle against its own manifest.sha256, forces the phase-one
# msys2-runtime reinstall, restarts the shell, installs the rest and requires the
# prefix to hold exactly the locked set. A consumer that re-implemented any of
# that would be a consumer nothing had gated.
$installEvidence = Join-Path $EvidenceDir 'install'
& (Join-Path $scriptDir 'install-locked.ps1') `
    -BundleDir $bundleDir -EvidenceDir $installEvidence -MsysRoot $MsysRoot
if ($LASTEXITCODE -ne 0) { Stop-Hard "install-locked.ps1 exited $LASTEXITCODE" }

$install = Get-Content -LiteralPath (Join-Path $installEvidence 'install-locked.json') -Raw |
    ConvertFrom-Json

$evidence = [ordered]@{
    probe             = 'w1r-consume'
    reference         = $Reference
    manifestDigest    = $digest
    layerDigest       = $layerDigest
    lockSha256        = $lockSha
    trustRootSha256   = $signatures.trustRootSha256
    signaturesVerified = $signatures.verified
    acceptedFingerprints = $signatures.acceptedFingerprints
    packageCount      = $lock.packageCount
    installedPackages = $install.installedAfter
    installedSetEqualsLock = $install.installedSetEqualsLock
    runtimeReinstall  = $install.runtimeReinstall
    mirrorsEmptied    = $install.mirrorsEmptied
    upstreamConsulted = $false
    pacmanMode        = 'pacman -U over local files only'
    tagUsed           = $false
}
$evidence | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $EvidenceDir 'consume.json') -Encoding utf8NoBOM

Write-Host "consumed ${Reference}: $($signatures.verified) signatures verified, $($install.installedAfter) packages installed, set equal to the lock"
