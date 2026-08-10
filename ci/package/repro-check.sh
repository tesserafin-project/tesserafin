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

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

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
