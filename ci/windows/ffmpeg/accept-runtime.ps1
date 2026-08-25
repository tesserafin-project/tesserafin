<#
.SYNOPSIS
    Behavioural acceptance for the win-x64 FFmpeg runtime (W1-A3, #236).

.DESCRIPTION
    The static gate (verify-runtime.py) reads bytes and runs anywhere. These
    checks need a real Windows machine to answer, so they live here and are not
    simulated:

      * the runtime is extracted from the DELIVERED ARCHIVE, to a directory that
        has nothing to do with where it was built, and used from there. A build
        tree that works in place proves nothing about relocation;
      * PATH is reduced to the system directories and the extracted bin, and the
        absence of any other ffmpeg on it is asserted rather than assumed. A
        machine with FFmpeg already installed would otherwise happily answer
        every question this script asks;
      * software encode -> probe -> decode, end to end, reading the results back
        rather than trusting an exit code;
      * the capability listing is taken from the extracted binary, not from the
        build tree's copy.

    Nothing here claims a hardware capability. The runtime is compiled with
    DXVA2, D3D11VA, D3D12VA, AMF, QSV and NVENC surfaces; whether a given
    machine can USE any of them is a property of that machine.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Archive,
    [Parameter(Mandatory = $true)][string]$WorkDir,
    [Parameter(Mandatory = $true)][string]$EvidenceDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$script:Failures = 0
function Fail { param([string]$m) Write-Host "  FAIL: $m"; $script:Failures++ }
function Ok   { param([string]$m) Write-Host "  ok  : $m" }

New-Item -ItemType Directory -Force -Path $WorkDir, $EvidenceDir | Out-Null

# ── 1. Relocation: extract somewhere unrelated to the build ──────────────────
$extract = Join-Path $WorkDir 'relocated'
if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
New-Item -ItemType Directory -Force -Path $extract | Out-Null
Expand-Archive -LiteralPath $Archive -DestinationPath $extract -Force

$ffmpeg = Join-Path $extract 'bin\ffmpeg.exe'
$ffprobe = Join-Path $extract 'bin\ffprobe.exe'
foreach ($exe in @($ffmpeg, $ffprobe)) {
    if (-not (Test-Path -LiteralPath $exe)) { Fail "the archive did not contain $exe"; }
}
if ($script:Failures) { throw "WIN-X64 ACCEPTANCE HARD STOP: the delivered archive is not a runtime" }
Ok "extracted to $extract, a path unrelated to the build"

# ── 2. A PATH with no other FFmpeg on it ─────────────────────────────────────
$originalPath = $env:PATH
$system = @(
    "$env:SystemRoot\system32",
    "$env:SystemRoot",
    "$env:SystemRoot\system32\Wbem"
) -join ';'
$env:PATH = $system

$foreign = @(Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue)
if ($foreign.Count -gt 0) {
    Fail "a system ffmpeg is reachable at $($foreign[0].Source); this acceptance would not be measuring the delivered runtime"
} else {
    Ok 'no system FFmpeg is on PATH'
}

# ── 3. Software encode -> probe -> decode ────────────────────────────────────
$media = Join-Path $WorkDir 'media'
New-Item -ItemType Directory -Force -Path $media | Out-Null
$sample = Join-Path $media 'sample.mp4'

& $ffmpeg -hide_banner -loglevel error -y `
    -f lavfi -i testsrc2=size=320x240:rate=25:duration=2 `
    -f lavfi -i sine=frequency=440:duration=2 `
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p `
    -c:a aac -b:a 64k `
    -shortest $sample
if ($LASTEXITCODE -ne 0) { Fail "libx264/aac encode exited $LASTEXITCODE" }
elseif (-not (Test-Path -LiteralPath $sample)) { Fail 'the encode produced no file' }
else { Ok "encoded $([math]::Round((Get-Item $sample).Length / 1KB, 1)) KB with libx264 + native aac" }

$probeJson = & $ffprobe -hide_banner -loglevel error -print_format json `
    -show_streams -show_format $sample | Out-String
if ($LASTEXITCODE -ne 0) { Fail "ffprobe exited $LASTEXITCODE" }

$probe = $null
try { $probe = $probeJson | ConvertFrom-Json } catch { Fail "ffprobe did not return JSON: $_" }
if ($null -ne $probe) {
    $video = @($probe.streams | Where-Object { $_.codec_type -eq 'video' })
    $audio = @($probe.streams | Where-Object { $_.codec_type -eq 'audio' })
    if ($video.Count -ne 1) { Fail "expected one video stream, found $($video.Count)" }
    elseif ($video[0].codec_name -ne 'h264') { Fail "video codec is $($video[0].codec_name), expected h264" }
    elseif ([int]$video[0].width -ne 320 -or [int]$video[0].height -ne 240) {
        Fail "video is $($video[0].width)x$($video[0].height), expected 320x240"
    } else { Ok "probe reads h264 320x240" }
    if ($audio.Count -ne 1) { Fail "expected one audio stream, found $($audio.Count)" }
    elseif ($audio[0].codec_name -ne 'aac') { Fail "audio codec is $($audio[0].codec_name), expected aac" }
    else { Ok "probe reads aac" }
}

# Decode every frame and count them, rather than trusting an exit code. 2 s at
# 25 fps is 50 frames; a decoder that silently produced 0 would still exit 0.
$decodeLog = & $ffmpeg -hide_banner -loglevel error -stats -i $sample `
    -f rawvideo -pix_fmt yuv420p NUL 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { Fail "decode exited $LASTEXITCODE" }
$frames = 0
if ($decodeLog -match 'frame=\s*(\d+)') { $frames = [int]$Matches[1] }
if ($frames -lt 45) { Fail "decode reported $frames frames, expected about 50" }
else { Ok "decoded $frames frames" }

# ── 4. Capabilities, read from the RELOCATED binary ──────────────────────────
$filters = & $ffmpeg -hide_banner -filters 2>&1 | Out-String
foreach ($required in @('tonemapx', 'zscale', 'ass', 'subtitles', 'alphasrc')) {
    if ($filters -notmatch "(?m)^\s*\S+\s+$([regex]::Escape($required))\s") {
        Fail "filter '$required' is absent from the relocated binary"
    } else { Ok "filter '$required' present" }
}
$encoders = & $ffmpeg -hide_banner -encoders 2>&1 | Out-String
if ($encoders -match 'fdk') { Fail 'an fdk encoder is present' } else { Ok 'no fdk encoder' }

$hwaccels = (& $ffmpeg -hide_banner -hwaccels 2>&1 | Out-String).Trim()
$buildconf = (& $ffmpeg -hide_banner -buildconf 2>&1 | Out-String).Trim()

$env:PATH = $originalPath

$evidence = [ordered]@{
    probe            = 'winx64-accept-runtime'
    archive          = (Resolve-Path -LiteralPath $Archive).Path
    archiveSha256    = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    relocatedTo      = $extract
    systemFfmpegOnPath = ($foreign.Count -gt 0)
    decodedFrames    = $frames
    hwaccels         = $hwaccels -split '\r?\n' | Where-Object { $_ -and $_ -notmatch 'Hardware' }
    buildConfiguration = $buildconf
    failures         = $script:Failures
    hardwareRuntimeClaim = 'none: compiled capability is not a hardware-runtime claim'
}
$evidence | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $EvidenceDir 'accept-runtime.json') -Encoding utf8NoBOM

if ($script:Failures -gt 0) {
    throw "WIN-X64 ACCEPTANCE: FAIL — $($script:Failures) check(s) failed"
}
Write-Host 'WIN-X64 ACCEPTANCE: PASS'
