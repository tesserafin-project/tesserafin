#!/usr/bin/env bash
# Negative controls for the package reproducibility comparison (#225 / [L0]).
#
# Usage: ci/package/repro-controls.sh --artifacts DIR --rid RID
#
# A reproducibility gate that has never been shown to fail is a gate nobody has
# tested. These controls take a REAL artifact set, damage it one way at a time,
# and require the comparison to reject each damaged copy. Nothing is rebuilt: the
# controls measure the comparison, not the build, and rebuilding would make them
# cost an hour to prove a property that takes seconds.
#
# Eight defects, each corresponding to a way two builds can look equal without
# being equal:
#
#   1. shortened reference list      — a line silently dropped
#   2. additional path               — an artifact one side did not produce
#   3. renamed path                  — same bytes, different delivered name
#   4. obsolete manifest format      — a v1 provenance manifest accepted as v2
#   5. corrupted package             — one artifact's bytes altered
#   6. corrupted provenance manifest — a digest edited to match nothing
#   7. mismatched source archive     — a different corresponding-source archive
#   8. architecture mismatch         — an arm64 manifest beside x64 artifacts

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ARTIFACTS=""; RID=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --artifacts) ARTIFACTS="$2"; shift 2 ;;
        --rid)       RID="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -d "${ARTIFACTS}" ]] || pkg_die "--artifacts must be an existing artifact directory"
[[ -n "${RID}" ]]       || pkg_die "--rid is required"

pkg_load_pins
pkg_load_version_contract

SUMS="SHA256SUMS-${RID}.txt"
[[ -f "${ARTIFACTS}/${SUMS}" ]] || pkg_die "no ${SUMS} in ${ARTIFACTS}"

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

PASSED=0; FAILED=0
control() { # <name> <expectation: reject|accept> <command...>
    local name="$1" expect="$2"; shift 2
    if "$@" >/dev/null 2>&1; then
        if [[ "${expect}" == "accept" ]]; then
            PASSED=$((PASSED + 1)); echo "  ok   ${name}: accepted, as it must be"
        else
            FAILED=$((FAILED + 1)); echo "  FAIL ${name}: the damaged set was ACCEPTED" >&2
        fi
    else
        if [[ "${expect}" == "reject" ]]; then
            PASSED=$((PASSED + 1)); echo "  ok   ${name}: rejected"
        else
            FAILED=$((FAILED + 1)); echo "  FAIL ${name}: an undamaged set was rejected" >&2
        fi
    fi
}

# The comparison under test, isolated from the build: two checksum manifests and
# the directory the second describes. Same rule repro-check.sh applies — path set
# first, then digests.
compare() { # <reference sums> <candidate sums>
    local ref="$1" cand="$2" ref_paths new_paths
    ref_paths="$(awk '{print $2}' "${ref}"  | LC_ALL=C sort)"
    new_paths="$(awk '{print $2}' "${cand}" | LC_ALL=C sort)"
    [[ "${ref_paths}" == "${new_paths}" ]] || return 1
    diff -q "${ref}" "${cand}" >/dev/null || return 1
}

fresh() { # <dir name> -> a pristine copy of the artifact set
    local dir="${WORK}/$1"
    rm -rf "${dir}"; cp -a "${ARTIFACTS}" "${dir}"
    printf '%s\n' "${dir}"
}

echo "== reproducibility comparison controls (${RID})"

# 0. The undamaged control. Without it, every "rejected" below could mean the
#    comparison rejects everything.
BASE="$(fresh base)"
control "undamaged set" accept compare "${ARTIFACTS}/${SUMS}" "${BASE}/${SUMS}"

# 1. Shortened reference list.
D="$(fresh short)"
sed -i '$d' "${D}/${SUMS}"
control "shortened reference list" reject compare "${ARTIFACTS}/${SUMS}" "${D}/${SUMS}"

# 2. Additional path.
D="$(fresh extra)"
printf '%064d  tesserafin-server-extra-%s.bin\n' 0 "${RID}" >> "${D}/${SUMS}"
control "additional delivered path" reject compare "${ARTIFACTS}/${SUMS}" "${D}/${SUMS}"

# 3. Renamed path, identical digest.
D="$(fresh renamed)"
sed -i -E "0,/\.tar\.gz$/s/(-${RID})\.tar\.gz$/\1-renamed.tar.gz/" "${D}/${SUMS}"
control "renamed delivered path" reject compare "${ARTIFACTS}/${SUMS}" "${D}/${SUMS}"

# 4. Obsolete manifest format: a v1 manifest with the upstream-asset fields the
#    repair removed. The schema gate must reject it rather than treat the missing
#    v2 keys as optional.
D="$(fresh obsolete)"
manifest="$(find "${D}" -name '*.deb.provenance.json' | head -1)"
python3 - "${manifest}" <<'PY'
import json, sys
p = sys.argv[1]
m = json.load(open(p))
m.pop("schemaVersion", None)
m.pop("ffmpegRuntime", None)
m.pop("licensing", None)
m["ffmpegVersion"] = "7.1.4-3"
m["ffmpegAsset"] = "jellyfin-ffmpeg_7.1.4-3_portable_linux64-gpl.tar.xz"
m["ffmpegSha256"] = "cab9ff40a47e4232d231e4eb7e4e85fabfeec56c6905266bc94291fc0881f83f"
json.dump(m, open(p, "w"), indent=2, sort_keys=True)
PY
control "obsolete v1 provenance manifest" reject \
    "${PKG_REPO_ROOT}/ci/package/verify-provenance.sh" --artifacts "${D}" --rid "${RID}"

# 5. Corrupted package.
D="$(fresh corrupt-pkg)"
target="$(find "${D}" -name "*_$(pkg_deb_arch "${RID}").deb" | head -1)"
printf '\0corrupted' >> "${target}"
( cd "${D}" && awk '{print $2}' "${SUMS}" | xargs -r sha256sum ) > "${D}/${SUMS}.new"
mv "${D}/${SUMS}.new" "${D}/${SUMS}"
control "corrupted package" reject compare "${ARTIFACTS}/${SUMS}" "${D}/${SUMS}"

# 6. Corrupted provenance manifest: the recorded digest no longer describes the
#    artifact beside it.
D="$(fresh corrupt-prov)"
manifest="$(find "${D}" -name '*.rpm.provenance.json' | head -1)"
python3 - "${manifest}" <<'PY'
import json, sys
p = sys.argv[1]
m = json.load(open(p))
m["artifactSha256"] = "0" * 64
json.dump(m, open(p, "w"), indent=2, sort_keys=True)
PY
control "corrupted provenance manifest" reject \
    "${PKG_REPO_ROOT}/ci/package/verify-provenance.sh" --artifacts "${D}" --rid "${RID}"

# 7. Mismatched source archive: the sidecar replaced by different bytes under the
#    same name. This is the "two different archives, one filename" case that a
#    later release assembler would deduplicate into a lie.
D="$(fresh bad-source)"
printf 'not the corresponding source' > "${D}/${F0_SOURCE_ARCHIVE}"
control "mismatched corresponding-source archive" reject \
    "${PKG_REPO_ROOT}/ci/package/verify-provenance.sh" --artifacts "${D}" --rid "${RID}"

# 8. Architecture mismatch: a manifest describing the other architecture's
#    runtime sitting beside these artifacts.
D="$(fresh arch-mismatch)"
other="linux-arm64"; [[ "${RID}" == "linux-arm64" ]] && other="linux-x64"
manifest="$(find "${D}" -name '*.tar.gz.provenance.json' | head -1)"
python3 - "${manifest}" "${other}" <<'PY'
import json, sys
p, other = sys.argv[1:3]
m = json.load(open(p))
m["ffmpegRuntime"]["architecture"] = other
json.dump(m, open(p, "w"), indent=2, sort_keys=True)
PY
control "architecture mismatch in a manifest" reject \
    "${PKG_REPO_ROOT}/ci/package/verify-provenance.sh" --artifacts "${D}" --rid "${RID}"

echo
echo "passed: ${PASSED}  failed: ${FAILED}"
[[ "${FAILED}" -eq 0 ]] || exit 1
echo "REPRO CONTROLS: PASS — ${PASSED} controls, ${RID}"
