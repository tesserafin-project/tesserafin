#!/usr/bin/env bash
# The SHA-256 of every delivered file, in a stable order (F0 / #229).
#
# Usage: ci/ffmpeg/delivered-digests.sh --pkg DIR --arch RID
#
# One definition of "delivered", used by both sides of the reproducibility
# comparison: the job that builds and the job that rebuilds. Keeping it here
# rather than inside repro-check.sh is what lets two DIFFERENT runners be
# compared — a second build on the same machine shares its kernel, its CPU
# model and its Docker state, and agreeing with itself is a weaker claim than
# agreeing with a machine it never met.
#
# Paths are printed relative to --pkg so the digests do not depend on where the
# build happened.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

PKG=""; ARCH=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --pkg)  PKG="$2"; shift 2 ;;
        --arch) ARCH="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${PKG}" ]] || ff_die "--pkg must be an existing packaged output directory"
[[ -n "${ARCH}" ]] || ff_die "--arch is required"

ff_load_manifest
NAME="tesserafin-ffmpeg-${FF_BUILD_REVISION}"

# Everything a recipient receives. The two archives, and the manifests inside
# the runtime that describe it: a build that produced identical binaries but a
# different SBOM has still not reproduced.
DELIVERED=(
    "${NAME}-${ARCH}/bin/ffmpeg"
    "${NAME}-${ARCH}/bin/ffprobe"
    "${NAME}-${ARCH}/SOURCE.json"
    "${NAME}-${ARCH}/sbom.cdx.json"
    "${NAME}-${ARCH}/capability.json"
    "${NAME}-${ARCH}/THIRD_PARTY_NOTICES.md"
    "${NAME}-${ARCH}.tar.xz"
    "${NAME}-corresponding-source.tar.zst"
)

for f in "${DELIVERED[@]}"; do
    if [[ -f "${PKG}/${f}" ]]; then
        printf '%s  %s\n' "$(ff_sha256 "${PKG}/${f}")" "${f}"
    else
        # Never silently omit: a missing file must break the comparison rather
        # than shrink both sides of it into agreement.
        printf 'MISSING  %s\n' "${f}"
    fi
done | LC_ALL=C sort -k2
