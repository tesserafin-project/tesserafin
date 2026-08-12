#!/usr/bin/env bash
# W0 native Windows FFmpeg source-build feasibility spike (#234, phase 4).
#
# W0-ONLY, DISPOSABLE, PROBE EVIDENCE ONLY. What this proves is that the pinned
# jellyfin-ffmpeg source at F0_UPSTREAM_COMMIT can be configured, patched and
# compiled to ffmpeg.exe / ffprobe.exe by a NATIVE Windows toolchain, and that
# the result executes software encode -> probe -> decode on that same host.
#
# What this is NOT:
#   * not the accepted Tesserafin Windows FFmpeg runtime;
#   * not a complete component closure — the full closure is DESIGN in W0 and
#     BUILD in W1. `--disable-autodetect` is deliberate: it bounds the spike to
#     the mechanism and stops MSYS2's ambient packages from silently entering
#     the link, which would make the result unattributable;
#   * not a hardware claim of any kind. A hosted runner has no GPU.
#
# Forbidden here and not present: any downloaded precompiled FFmpeg, Wine, a
# system FFmpeg substitution, any unpinned MSYS2/vcpkg/Chocolatey dependency.

set -o errexit
set -o nounset
set -o pipefail

FFMPEG_COMMIT="${FFMPEG_COMMIT:?FFMPEG_COMMIT must be the pinned upstream commit}"
EVIDENCE_DIR="${EVIDENCE_DIR:?EVIDENCE_DIR must be set}"
WORK_DIR="${WORK_DIR:-/tmp/w0-ffmpeg}"

mkdir -p "${EVIDENCE_DIR}" "${WORK_DIR}"

json_escape() { python -c 'import json,sys;print(json.dumps(sys.stdin.read()))'; }

# ── Toolchain provenance ──────────────────────────────────────────────────────
#
# Recorded BEFORE anything is built. MSYS2's pacman repository is rolling, so the
# exact package versions resolved on this run are the only thing that makes the
# resulting binary attributable — and their absence from any pin file is exactly
# the reproducibility gap W1 has to close.

{
  echo "=== uname ==="
  uname -a
  echo "=== clang ==="
  clang --version
  echo "=== nasm ==="
  nasm -v || true
  echo "=== make ==="
  make --version | head -1
  echo "=== resolved MSYS2 packages (UNPINNED UPSTREAM — this is the W1 gap) ==="
  pacman -Q | sed 's/^/pkg /'
} > "${EVIDENCE_DIR}/toolchain.txt" 2>&1

# ── Pinned source ─────────────────────────────────────────────────────────────

cd "${WORK_DIR}"
if [[ ! -d jellyfin-ffmpeg ]]; then
  git init jellyfin-ffmpeg
  git -C jellyfin-ffmpeg remote add origin https://github.com/jellyfin/jellyfin-ffmpeg.git
fi
git -C jellyfin-ffmpeg fetch --depth 1 origin "${FFMPEG_COMMIT}"
git -C jellyfin-ffmpeg checkout --force FETCH_HEAD

resolved="$(git -C jellyfin-ffmpeg rev-parse HEAD)"
if [[ "${resolved}" != "${FFMPEG_COMMIT}" ]]; then
  echo "W0 HARD STOP: checked out ${resolved}, expected ${FFMPEG_COMMIT}" >&2
  exit 1
fi

cd jellyfin-ffmpeg

# The same quilt patch series the Linux build applies. If the fork's patches did
# not apply on a native Windows checkout, the whole "one fork, two targets"
# premise would collapse and W0 would have to say so.
patch_status="not-applied"
patch_count=0
if [[ -f debian/patches/series ]]; then
  patch_count="$(grep -cvE '^\s*(#|$)' debian/patches/series || true)"
  ln -sf debian/patches patches
  if quilt push -a > "${EVIDENCE_DIR}/quilt.log" 2>&1; then
    patch_status="applied"
  else
    patch_status="failed"
  fi
fi

# ── Configure and build ───────────────────────────────────────────────────────

PREFIX="${WORK_DIR}/prefix"

# Mechanism-only configure. No external media library is enabled, so nothing here
# is a capability claim; the software encoders and decoders used by the smoke
# below are FFmpeg's own. Licence shape still stated explicitly rather than left
# to defaults, matching ci/ffmpeg/ffmpeg-configure.txt.
./configure \
  --cc=clang \
  --prefix="${PREFIX}" \
  --disable-autodetect \
  --disable-doc \
  --disable-debug \
  --disable-ffplay \
  --disable-sdl2 \
  --disable-nonfree \
  --disable-libfdk-aac \
  --enable-gpl \
  --enable-version3 \
  --disable-shared \
  --enable-static \
  > "${EVIDENCE_DIR}/configure.log" 2>&1 || {
    echo "W0: configure failed; see configure.log" >&2
    tail -40 "${EVIDENCE_DIR}/configure.log" >&2
    exit 1
  }

make -j"$(nproc)" ffmpeg.exe ffprobe.exe > "${EVIDENCE_DIR}/make.log" 2>&1 || {
  echo "W0: make failed; see make.log" >&2
  tail -60 "${EVIDENCE_DIR}/make.log" >&2
  exit 1
}

test -f ffmpeg.exe
test -f ffprobe.exe

# ── Inspect the produced images ───────────────────────────────────────────────

./ffmpeg.exe -hide_banner -version   > "${EVIDENCE_DIR}/ffmpeg-version.txt"   2>&1
./ffmpeg.exe -hide_banner -buildconf > "${EVIDENCE_DIR}/ffmpeg-buildconf.txt" 2>&1
./ffmpeg.exe -hide_banner -encoders  > "${EVIDENCE_DIR}/ffmpeg-encoders.txt"  2>&1
./ffmpeg.exe -hide_banner -decoders  > "${EVIDENCE_DIR}/ffmpeg-decoders.txt"  2>&1
./ffmpeg.exe -hide_banner -filters   > "${EVIDENCE_DIR}/ffmpeg-filters.txt"   2>&1
./ffmpeg.exe -hide_banner -protocols > "${EVIDENCE_DIR}/ffmpeg-protocols.txt" 2>&1
./ffmpeg.exe -hide_banner -hwaccels  > "${EVIDENCE_DIR}/ffmpeg-hwaccels.txt"  2>&1

# PE architecture and the DLL closure, read off the images themselves. A static
# build should resolve nothing beyond the OS DLLs; whatever it does name is the
# redistribution question W1 inherits.
{
  echo "=== ffmpeg.exe ==="
  file ffmpeg.exe || true
  echo "--- imports ---"
  objdump -p ffmpeg.exe | grep -i 'DLL Name' || true
  echo "=== ffprobe.exe ==="
  file ffprobe.exe || true
  echo "--- imports ---"
  objdump -p ffprobe.exe | grep -i 'DLL Name' || true
} > "${EVIDENCE_DIR}/pe-closure.txt" 2>&1

# ── Software encode -> probe -> decode smoke ──────────────────────────────────

smoke_dir="${WORK_DIR}/smoke"
rm -rf "${smoke_dir}"
mkdir -p "${smoke_dir}"

smoke_status="fail"
if ./ffmpeg.exe -hide_banner -nostdin -y \
      -f lavfi -i "testsrc2=size=320x240:rate=25:duration=2" \
      -f lavfi -i "sine=frequency=440:duration=2" \
      -c:v mpeg4 -c:a aac -shortest \
      "${smoke_dir}/smoke.mp4" > "${EVIDENCE_DIR}/smoke-encode.log" 2>&1 \
   && ./ffprobe.exe -hide_banner -v error -show_format -show_streams \
      -of json "${smoke_dir}/smoke.mp4" > "${EVIDENCE_DIR}/smoke-probe.json" 2>&1 \
   && ./ffmpeg.exe -hide_banner -nostdin -y \
      -i "${smoke_dir}/smoke.mp4" -f null - > "${EVIDENCE_DIR}/smoke-decode.log" 2>&1; then
  smoke_status="pass"
fi

encoded_bytes="$(stat -c %s "${smoke_dir}/smoke.mp4" 2>/dev/null || echo 0)"

# ── Verdict ───────────────────────────────────────────────────────────────────

cat > "${EVIDENCE_DIR}/ffmpeg-spike.json" <<EOF
{
  "probe": "ffmpeg-spike",
  "upstreamCommit": "${resolved}",
  "mechanism": "native Windows MSYS2 CLANG64, the upstream msys2/build.sh path",
  "patchSeries": { "status": "${patch_status}", "count": ${patch_count} },
  "smoke": { "status": "${smoke_status}", "encodedBytes": ${encoded_bytes} },
  "hardwareClaim": "none — a hosted runner has no GPU and this spike enables no hardware backend",
  "isAcceptedRuntime": false,
  "note": "Disposable W0 feasibility evidence. --disable-autodetect bounds this to the build mechanism; the component closure is designed in W0 and built in W1."
}
EOF

echo "W0 ffmpeg spike: patches=${patch_status} smoke=${smoke_status} bytes=${encoded_bytes}"
[[ "${smoke_status}" == "pass" ]]
