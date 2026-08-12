#!/usr/bin/env bash
# Reproducibility gate for the native Linux artifacts (#225 / [L0]).
#
# Usage: ci/package/repro-check.sh --rid RID [--against SHA256SUMS-file]
#
# Builds every artifact for one architecture from a clean tree and compares the
# SHA-256 of each against a reference set — either a second build performed here,
# or the checksum file produced by an earlier, independent build.
#
# The verdict is bit-for-bit or nothing. On a mismatch the differing artifacts
# are named, and their members are diffed far enough to point at the cause; the
# gate is never downgraded to "functionally equivalent".
#
# The delivered PATH SET is compared before any digest. Two builds that agree on
# every digest they both produced have still not reproduced if one of them
# produced fewer files — a shortened reference list is the failure mode a
# digest-only comparison cannot see.
#
# PKG_REPRO=1 is exported for every build this script runs. It makes
# ci/package/ffmpeg-runtime.sh refuse `--reuse`, so each side rebuilds the FFmpeg
# runtime from source rather than sharing one. An independent rebuild that reuses
# anything has not rebuilt.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

export PKG_REPRO=1

RID=""; AGAINST=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)     RID="$2"; shift 2 ;;
        --against) AGAINST="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -n "${RID}" ]] || pkg_die "--rid is required"

pkg_load_pins
pkg_load_version_contract

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

if [[ -z "${AGAINST}" ]]; then
    pkg_log "reproducibility: first clean build of ${RID}"
    "${PKG_REPO_ROOT}/ci/package/build-all.sh" --rid "${RID}" --out "${WORK}/b1" >/dev/null
    AGAINST="${WORK}/b1/artifacts/SHA256SUMS-${RID}.txt"
    FIRST_DIR="${WORK}/b1/artifacts"
else
    [[ -f "${AGAINST}" ]] || pkg_die "reference checksum file not found: ${AGAINST}"
    FIRST_DIR="$(cd "$(dirname "${AGAINST}")" && pwd)"
    pkg_log "reproducibility: comparing against ${AGAINST}"
fi

pkg_log "reproducibility: second clean build of ${RID}"
"${PKG_REPO_ROOT}/ci/package/build-all.sh" --rid "${RID}" --out "${WORK}/b2" >/dev/null
SECOND="${WORK}/b2/artifacts/SHA256SUMS-${RID}.txt"

echo
echo "-- reference --"; cat "${AGAINST}"
echo "-- rebuild   --"; cat "${SECOND}"
echo

# --- the delivered path set, before any digest --------------------------------
#
# A reference list that lost a line, gained one, or renamed one describes a
# different delivery. Comparing digests first would report "no differing
# digests" for a reference that simply stopped mentioning an artifact.
ref_paths="$(awk '{print $2}' "${AGAINST}" | LC_ALL=C sort)"
new_paths="$(awk '{print $2}' "${SECOND}"  | LC_ALL=C sort)"
if [[ "${ref_paths}" != "${new_paths}" ]]; then
    echo "REPRO: FAIL — the two builds do not deliver the same set of paths" >&2
    diff <(echo "${ref_paths}") <(echo "${new_paths}") >&2 || true
    exit 1
fi
ref_count="$(wc -l <<<"${ref_paths}")"
echo "delivered path sets agree: ${ref_count} paths on both sides"

# The corresponding-source archive is architecture-independent, so it must be in
# the set and both sides must have produced the same bytes. Two different
# archives under one name is precisely the silent divergence this forbids.
if ! grep -q "${F0_SOURCE_ARCHIVE}" <<<"${ref_paths}"; then
    echo "REPRO: FAIL — the corresponding-source archive is not in the delivered set" >&2
    exit 1
fi

if diff -q "${AGAINST}" "${SECOND}" >/dev/null; then
    echo "REPRO: PASS — every ${RID} artifact is bit-for-bit identical across two clean builds"
    exit 0
fi

echo "REPRO: MISMATCH — localising the divergence" >&2
diff "${AGAINST}" "${SECOND}" >&2 || true

while read -r _ name; do
    a="${FIRST_DIR}/${name}"
    b="${WORK}/b2/artifacts/${name}"
    [[ -f "${a}" && -f "${b}" ]] || continue
    if ! cmp -s "${a}" "${b}"; then
        echo "--- differing artifact: ${name}" >&2
        case "${name}" in
            *.deb)
                diff <(dpkg-deb -c "${a}") <(dpkg-deb -c "${b}") >&2 || true
                diff <(dpkg-deb -f "${a}") <(dpkg-deb -f "${b}") >&2 || true
                ;;
            *.tar.gz)
                diff <(tar -tvzf "${a}") <(tar -tvzf "${b}") >&2 || true
                ;;
            *.rpm)
                tools="$(pkg_rpm_builder_image)"
                diff <(docker run --rm -v "${FIRST_DIR}:/a:ro" "${tools}" \
                          rpm -qp --nosignature --dump "/a/${name}") \
                     <(docker run --rm -v "${WORK}/b2/artifacts:/b:ro" "${tools}" \
                          rpm -qp --nosignature --dump "/b/${name}") >&2 || true
                ;;
        esac
    fi
done < "${AGAINST}"

exit 1
