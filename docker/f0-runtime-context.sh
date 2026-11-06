#!/usr/bin/env bash
# Prepares the `ffmpeg-runtime` named build context the Dockerfile consumes (#342).
#
# Usage:
#   docker/f0-runtime-context.sh --out DIR [--rid linux-x64|linux-arm64]...
#                                [--from PKGDIR]...
#
#   --rid RID     build the accepted F0 runtime for RID on THIS machine with
#                 ci/package/ffmpeg-runtime.sh (native only — F0 refuses
#                 emulation). Repeatable; default: the host's own RID.
#   --from DIR    take the archive(s) from an existing F0 package directory
#                 (ci/package/ffmpeg-runtime.sh --out X  =>  X/pkg) instead of
#                 building. Use it to bring in an archive built on another
#                 machine, e.g. linux-arm64 from a native arm64 runner.
#   --out DIR     working directory; the context is DIR/context.
#
# The context holds ONLY the F0 runtime archives, nothing else. It is not
# trusted: the Dockerfile accepts an archive only if its SHA-256 is the one
# recorded in ci/package/f0-accepted-digests.txt, so a context prepared from a
# fresh build, a cache or another runner yields the same image bytes or no
# image at all.
#
# Prints the context directory as its last line, for
#   docker build --build-context ffmpeg-runtime="$(docker/f0-runtime-context.sh ... | tail -1)" .
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

OUT=""; RIDS=(); FROMS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out)  OUT="$2"; shift 2 ;;
    --rid)  RIDS+=("$2"); shift 2 ;;
    --from) FROMS+=("$2"); shift 2 ;;
    *) echo "f0-runtime-context: unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ -n "${OUT}" ]] || { echo "f0-runtime-context: --out is required" >&2; exit 2; }

if [[ ${#RIDS[@]} -eq 0 && ${#FROMS[@]} -eq 0 ]]; then
  case "$(uname -m)" in
    x86_64)  RIDS=(linux-x64) ;;
    aarch64) RIDS=(linux-arm64) ;;
    *) echo "f0-runtime-context: no F0 runtime for $(uname -m)" >&2; exit 2 ;;
  esac
fi

mkdir -p "${OUT}"
OUT="$(cd "${OUT}" && pwd)"
CTX="${OUT}/context"
rm -rf "${CTX}"; mkdir -p "${CTX}"

take() { # <pkg dir>
  local pkg="$1" found=0 a
  shopt -s nullglob
  for a in "${pkg}"/tesserafin-ffmpeg-*-linux-x64.tar.xz "${pkg}"/tesserafin-ffmpeg-*-linux-arm64.tar.xz; do
    if [[ -e "${CTX}/$(basename "${a}")" ]]; then
      echo "f0-runtime-context: $(basename "${a}") supplied twice" >&2; exit 1
    fi
    cp --reflink=auto "${a}" "${CTX}/"
    echo "  + $(basename "${a}")  $(sha256sum "${a}" | cut -d' ' -f1)" >&2
    found=1
  done
  shopt -u nullglob
  [[ "${found}" -eq 1 ]] || { echo "f0-runtime-context: no F0 runtime archive under ${pkg}" >&2; exit 1; }
}

for rid in "${RIDS[@]+"${RIDS[@]}"}"; do
  echo "== building the accepted F0 runtime for ${rid} (ci/package/ffmpeg-runtime.sh)" >&2
  "${REPO_ROOT}/ci/package/ffmpeg-runtime.sh" --rid "${rid}" --out "${OUT}/build-${rid}" --reuse >&2
  take "${OUT}/build-${rid}/pkg"
done
for from in "${FROMS[@]+"${FROMS[@]}"}"; do
  take "${from}"
done

echo "${CTX}"
