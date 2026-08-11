#!/usr/bin/env bash
# Reproducibility gate for the Tesserafin FFmpeg runtime (F0 / #229).
#
# Usage:
#   ci/ffmpeg/repro-check.sh --arch RID --out DIR                 # build twice here
#   ci/ffmpeg/repro-check.sh --arch RID --out DIR --against FILE  # compare with an earlier build's SHA256SUMS
#
# The verdict is bit-for-bit or nothing. Every delivered file is compared:
# ffmpeg, ffprobe, the runtime archive, the SBOM, SOURCE.json, the capability
# manifest and the corresponding-source archive. "Functionally equivalent" is
# not a passing result and this script has no way to report one.
#
# Each build is a full clean build. FF_INCREMENTAL is explicitly cleared: a
# reused prefix would make two builds agree for the wrong reason, which is
# exactly the failure this gate exists to catch.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ARCH=""; OUT=""; AGAINST=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch)    ARCH="$2"; shift 2 ;;
        --out)     OUT="$2"; shift 2 ;;
        --against) AGAINST="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -n "${ARCH}" ]] || ff_die "--arch is required"
[[ -n "${OUT}" ]]  || ff_die "--out is required"

ff_load_manifest
export FF_INCREMENTAL=0
NAME="tesserafin-ffmpeg-${FF_BUILD_REVISION}"

# Delegated to ci/ffmpeg/delivered-digests.sh so the job that BUILDS and the job
# that REBUILDS compute the same list from the same definition. That is what
# makes --against a real comparison between two different runners rather than a
# machine agreeing with itself.
digests() { # <build dir>
    "${FF_REPO_ROOT}/ci/ffmpeg/delivered-digests.sh" --pkg "$1/pkg" --arch "${ARCH}"
}

# The delivered file NAMES alone, for validating a reference list before any
# digest is compared. --pkg /nonexistent makes every entry MISSING, which is
# exactly right here: only the second column is read.
ci_delivered_names() {
    "${FF_REPO_ROOT}/ci/ffmpeg/delivered-digests.sh" --pkg "${OUT}" --arch "${ARCH}" \
        | awk '{print $2}' | LC_ALL=C sort
}

one_build() { # <output dir>
    local dir="$1"
    # The build root is destroyed first, every time. FF_SOURCE_CACHE, when set,
    # holds VERIFIED SOURCE outside that root so a restored cache survives this
    # line; it carries no compiled output, so the compilation below is as clean
    # as it was before. fetch-sources.sh re-verifies every byte of it anyway.
    local cache="${FF_SOURCE_CACHE:-${dir}/source-cache}"
    rm -rf "${dir}"; mkdir -p "${dir}"
    "${FF_REPO_ROOT}/ci/ffmpeg/build-runtime.sh" --arch "${ARCH}" --out "${dir}" --cache "${cache}" >&2
    "${FF_REPO_ROOT}/ci/ffmpeg/verify-runtime.sh" \
        --stage "${dir}/${NAME}-${ARCH}" --arch "${ARCH}" \
        --manifest "${dir}/${NAME}-${ARCH}/capability.json" >&2
    "${FF_REPO_ROOT}/ci/ffmpeg/package-runtime.sh" \
        --stage "${dir}/${NAME}-${ARCH}" --cache "${cache}" \
        --out "${dir}/pkg" --arch "${ARCH}" >&2
}

mkdir -p "${OUT}"
if [[ -n "${AGAINST}" ]]; then
    [[ -f "${AGAINST}" ]] || ff_die "reference digest file not found: ${AGAINST}"
    # A reference in a different shape would "differ" from this build for
    # reasons that have nothing to do with reproducibility, and a reference
    # listing FEWER files would let a missing artifact pass unnoticed. Require
    # the same file set, by name, before comparing any digest.
    expected="$(ci_delivered_names)"
    actual="$(awk '{print $2}' "${AGAINST}" | LC_ALL=C sort)"
    if [[ "${expected}" != "${actual}" ]]; then
        echo "the reference digest list does not describe the same delivered files:" >&2
        diff <(printf '%s\n' "${expected}") <(printf '%s\n' "${actual}") >&2 || true
        ff_die "refusing to compare ${AGAINST} against a differently shaped list"
    fi
    cp "${AGAINST}" "${OUT}/first.sha256"
    ff_log "reproducibility: comparing against ${AGAINST}"
else
    ff_log "reproducibility: first clean build of ${ARCH}"
    one_build "${OUT}/b1"
    digests "${OUT}/b1" > "${OUT}/first.sha256"
fi

ff_log "reproducibility: second clean build of ${ARCH}"
one_build "${OUT}/b2"
digests "${OUT}/b2" > "${OUT}/second.sha256"

echo
echo "-- first  --"; cat "${OUT}/first.sha256"
echo "-- second --"; cat "${OUT}/second.sha256"
echo

if diff -q "${OUT}/first.sha256" "${OUT}/second.sha256" >/dev/null; then
    echo "REPRO: PASS — every delivered ${ARCH} file is bit-for-bit identical across two clean builds"
    exit 0
fi

echo "REPRO: MISMATCH — localising" >&2
diff "${OUT}/first.sha256" "${OUT}/second.sha256" >&2 || true

# Point at the cause rather than only reporting a digest difference.
while read -r _ name; do
    a="${OUT}/b1/pkg/${name}"; b="${OUT}/b2/pkg/${name}"
    [[ -f "${a}" && -f "${b}" ]] || continue
    cmp -s "${a}" "${b}" && continue
    echo "--- differing: ${name}" >&2
    case "${name}" in
        *.json|*.md) diff "${a}" "${b}" >&2 || true ;;
        *.tar.xz)    diff <(tar -tvJf "${a}") <(tar -tvJf "${b}") >&2 || true ;;
        *.tar.zst)   diff <(tar -tv --zstd -f "${a}") <(tar -tv --zstd -f "${b}") >&2 || true ;;
        *)           cmp -l "${a}" "${b}" | head -20 >&2 || true ;;
    esac
done < "${OUT}/first.sha256"

exit 1
