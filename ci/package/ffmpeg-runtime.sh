#!/usr/bin/env bash
# Produces the accepted Tesserafin FFmpeg runtime as a PACKAGE INPUT (#225 / [L0]).
#
# Usage: ci/package/ffmpeg-runtime.sh --rid linux-x64|linux-arm64 --out DIR [--reuse]
#
# This is an ADAPTER, not a second FFmpeg build. Every configure flag, component
# pin, patch decision, dependency list and version constant lives in ci/ffmpeg/**
# and is used from there; nothing is restated here. What this script adds is the
# package-side contract: the runtime a .deb, .rpm or .tar.gz is allowed to carry
# is the ACCEPTED one, built here, for THIS architecture, complete.
#
# It runs the merged F0 scripts in the F0 workflow's own order:
#
#   ci/ffmpeg/build-runtime.sh      build from the pinned sources, in the pinned
#                                   builder, with no network
#   ci/ffmpeg/verify-runtime.sh     portability and licence contract + capability
#   ci/ffmpeg/package-runtime.sh    corresponding source, notices, SBOM, archives
#   ci/ffmpeg/verify-closure.sh     redistribution closure
#   ci/ffmpeg/delivered-digests.sh  the delivered inventory, F0's own definition
#
# What it refuses:
#   * a runtime revision that is not the accepted one;
#   * a runtime architecture that is not the package RID;
#   * a failed F0 closure gate;
#   * an absent or additional F0 delivered path;
#   * a wrong ELF machine for ffmpeg or ffprobe;
#   * a RUNPATH that is not exactly $ORIGIN/../lib;
#   * a bundled SONAME symlink that does not resolve inside the runtime;
#   * a missing corresponding-source archive;
#   * a delivered digest that differs from the accepted baseline while
#     ci/ffmpeg/** is unchanged.
#
# It downloads no release asset, consults no workflow run, and unpacks no
# previously accepted binary. The accepted digests in
# ci/package/f0-accepted-digests.txt are a comparison ORACLE and are never an
# input to anything that is built.
#
# Prints the packaged runtime directory (the F0 --out) as its last line.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

RID=""; OUT=""; REUSE=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)   RID="$2"; shift 2 ;;
        --out)   OUT="$2"; shift 2 ;;
        --reuse) REUSE=1; shift ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -n "${RID}" ]] || pkg_die "--rid is required"
[[ -n "${OUT}" ]] || pkg_die "--out is required"
pkg_deb_arch "${RID}" >/dev/null   # validates the runtime identifier

pkg_load_pins

# --reuse lets the .deb, the .rpm and the portable archive for ONE architecture
# share ONE freshly built runtime within a single job. It must never reach the
# reproducibility path: an independent rebuild that reuses anything has not
# rebuilt. PKG_REPRO=1 makes that structural rather than a matter of care.
if [[ "${REUSE}" -eq 1 && "${PKG_REPRO:-0}" == "1" ]]; then
    pkg_die "--reuse is refused under PKG_REPRO=1: the reproducibility side must rebuild the runtime"
fi

# FF_INCREMENTAL keeps a dependency prefix across runs. It is a development
# convenience in F0 and is meaningless — worse, misleading — in anything that
# produces evidence, so it is refused outright here rather than passed through.
[[ "${FF_INCREMENTAL:-0}" == "0" ]] || pkg_die \
    "FF_INCREMENTAL is set: package runtimes are always built from a clean state"

RUNTIME_DIR_NAME="$(pkg_f0_runtime_dir_name "${RID}")"
STAGE="${OUT}/stage/${RUNTIME_DIR_NAME}"
PKG="${OUT}/pkg"
RT="${PKG}/${RUNTIME_DIR_NAME}"
SRC_ARCHIVE="${PKG}/${F0_SOURCE_ARCHIVE}"

# The runtime is never cross-built or emulated; F0 refuses that itself. Saying so
# here turns an opaque failure deep inside the builder into one line.
HOST_MACHINE="$(uname -m)"
case "${RID}" in
    linux-x64)   want_machine="x86_64" ;;
    linux-arm64) want_machine="aarch64" ;;
esac
[[ "${HOST_MACHINE}" == "${want_machine}" ]] || pkg_die \
    "this host is ${HOST_MACHINE}; the ${RID} runtime must be built on ${want_machine}. It is never cross-built or emulated."

# =============================================================================
# 1. Build, or reuse a complete runtime this job already built
# =============================================================================
if [[ "${REUSE}" -eq 1 && -f "${RT}/bin/ffmpeg" && -f "${SRC_ARCHIVE}" ]]; then
    pkg_log "reusing the runtime already built in ${PKG} for ${RID}"
else
    pkg_log "building the accepted FFmpeg runtime ${F0_BUILD_REVISION} for ${RID}"
    rm -rf "${OUT}/stage" "${PKG}"
    mkdir -p "${OUT}/stage"

    env -u FF_INCREMENTAL "${PKG_REPO_ROOT}/ci/ffmpeg/build-runtime.sh" \
        --arch "${RID}" --out "${OUT}/stage"

    pkg_log "F0 portability and licence contract"
    "${PKG_REPO_ROOT}/ci/ffmpeg/verify-runtime.sh" \
        --stage "${STAGE}" --arch "${RID}" --manifest "${STAGE}/capability.json"

    pkg_log "F0 redistribution closure"
    "${PKG_REPO_ROOT}/ci/ffmpeg/package-runtime.sh" \
        --stage "${STAGE}" \
        --cache "${OUT}/stage/source-cache" \
        --out   "${PKG}" \
        --arch  "${RID}"
    "${PKG_REPO_ROOT}/ci/ffmpeg/verify-closure.sh" \
        --runtime "${RT}" --source-archive "${SRC_ARCHIVE}"
fi

# =============================================================================
# 2. The package-side contract
# =============================================================================
FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

echo "== accepted-runtime contract (${RID})"

# 2a. Revision and upstream commit, read back from what was actually produced.
#     SOURCE.json is written by F0 from the manifest, so this closes the loop
#     between "what the pins say" and "what the artifact says about itself".
[[ -f "${RT}/SOURCE.json" ]] || pkg_die "the packaged runtime has no SOURCE.json"
read -r built_revision built_commit built_arch built_src_name built_src_sha <<<"$(
    python3 - "${RT}/SOURCE.json" <<'PY'
import json, sys
s = json.load(open(sys.argv[1]))
print(s["buildRevision"], s["ffmpeg"]["commit"], s["architecture"],
      s["correspondingSource"]["archive"], s["correspondingSource"]["sha256"])
PY
)"

if [[ "${built_revision}" == "${F0_RUNTIME_REVISION}" ]]; then
    pass "runtime revision is the accepted ${built_revision}"
else
    fail "runtime revision ${built_revision} is not the accepted ${F0_RUNTIME_REVISION}"
fi
if [[ "${built_commit}" == "${F0_UPSTREAM_COMMIT}" ]]; then
    pass "upstream commit is the accepted ${built_commit}"
else
    fail "upstream commit ${built_commit} is not the accepted ${F0_UPSTREAM_COMMIT}"
fi
if [[ "${built_arch}" == "${RID}" ]]; then
    pass "runtime architecture matches the package RID (${RID})"
else
    fail "runtime architecture ${built_arch} does not match the package RID ${RID}"
fi

# 2b. The corresponding source exists and is the one the runtime names.
if [[ -f "${SRC_ARCHIVE}" ]]; then
    actual_src_sha="$(pkg_sha256 "${SRC_ARCHIVE}")"
    if [[ "${built_src_name}" == "${F0_SOURCE_ARCHIVE}" && "${actual_src_sha}" == "${built_src_sha}" ]]; then
        pass "corresponding source ${built_src_name} present and matches SOURCE.json (${actual_src_sha})"
    else
        fail "corresponding source disagrees with SOURCE.json: file ${F0_SOURCE_ARCHIVE}/${actual_src_sha}, recorded ${built_src_name}/${built_src_sha}"
    fi
else
    fail "the corresponding-source archive ${F0_SOURCE_ARCHIVE} is missing"
fi

# 2c. Every F0 delivered path present, none additional.
#     "Additional" is measured over the runtime's TOP-LEVEL entries: the contents
#     of lib/ and LICENSES/ are a function of the component set, which is F0's to
#     declare, but an unexpected top-level entry is a layout change and must be
#     reviewed rather than absorbed.
delivered="$("${PKG_REPO_ROOT}/ci/ffmpeg/delivered-digests.sh" --pkg "${PKG}" --arch "${RID}")"
if grep -q '^MISSING' <<<"${delivered}"; then
    fail "F0 delivered paths are absent:"$'\n'"$(grep '^MISSING' <<<"${delivered}")"
else
    pass "every F0 delivered path is present (8 paths)"
fi

expected_top="LICENSES bin build-configuration.txt capability.json lib sbom.cdx.json SOURCE.json THIRD_PARTY_NOTICES.md"
actual_top="$(cd "${RT}" && find . -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort | tr '\n' ' ')"
expected_top_sorted="$(tr ' ' '\n' <<<"${expected_top}" | grep -v '^$' | LC_ALL=C sort | tr '\n' ' ')"
if [[ "${actual_top}" == "${expected_top_sorted}" ]]; then
    pass "runtime top-level layout is exactly the expected set"
else
    fail "runtime top-level layout drift: expected '${expected_top_sorted}', got '${actual_top}'"
fi

# 2d. ELF machine. A runtime built for the wrong architecture would install and
#     then fail at the first transcode, which is the worst possible moment.
want_elf="$(pkg_elf_machine "${RID}")"
for binary in ffmpeg ffprobe; do
    if [[ ! -x "${RT}/bin/${binary}" ]]; then
        fail "${binary} is missing from the packaged runtime"
        continue
    fi
    machine="$(readelf -h "${RT}/bin/${binary}" | sed -nE 's/^ *Machine: *//p')"
    if [[ "${machine}" == "${want_elf}" ]]; then
        pass "${binary} ELF machine is ${machine}"
    else
        fail "${binary} ELF machine is '${machine}', ${RID} requires '${want_elf}'"
    fi
done

# 2e. RUNPATH and the bundled libraries. The whole point of the F0 layout is that
#     bin/ and lib/ are siblings and the binary finds its own libraries through
#     $ORIGIN. Flattening the two would silently hand the process to whatever
#     libva the host happens to have.
for binary in ffmpeg ffprobe; do
    [[ -x "${RT}/bin/${binary}" ]] || continue
    runpath="$(readelf -d "${RT}/bin/${binary}" | sed -nE 's/.*\(RUNPATH\).*\[(.*)\]/\1/p')"
    if [[ "${runpath}" == '$ORIGIN/../lib' ]]; then
        pass "${binary} RUNPATH is exactly \$ORIGIN/../lib"
    else
        fail "${binary} RUNPATH is '${runpath}', expected '\$ORIGIN/../lib'"
    fi
done

if [[ -d "${RT}/lib" ]]; then
    unresolved=0
    while IFS= read -r link; do
        [[ -n "${link}" ]] || continue
        if [[ ! -e "${link}" ]]; then
            fail "bundled SONAME symlink does not resolve inside the runtime: ${link#"${RT}/"} -> $(readlink "${link}")"
            unresolved=$((unresolved + 1))
        elif [[ "$(readlink "${link}")" == /* ]]; then
            fail "bundled SONAME symlink is absolute: ${link#"${RT}/"} -> $(readlink "${link}")"
            unresolved=$((unresolved + 1))
        fi
    done < <(find "${RT}/lib" -type l)
    if [[ "${unresolved}" -eq 0 ]]; then
        pass "every bundled SONAME symlink resolves inside the runtime ($(find "${RT}/lib" -type l | wc -l) link(s), $(find "${RT}/lib" -type f | wc -l) file(s))"
    fi
else
    fail "the packaged runtime has no lib/ directory: the bundled libraries \$ORIGIN/../lib resolves to are absent"
fi

# 2f. The accepted digest baseline.
baseline_state="$(pkg_f0_baseline_state)"
if [[ ! -f "${PKG_F0_BASELINE}" ]]; then
    fail "missing the accepted digest baseline ${PKG_F0_BASELINE}"
elif [[ "${baseline_state}" == "current" ]]; then
    expected="$(grep -E "  ${F0_RUNTIME_NAME}-${RID}/|  ${F0_RUNTIME_NAME}-${RID}\.tar\.xz$|  ${F0_SOURCE_ARCHIVE}$" \
                "${PKG_F0_BASELINE}" | LC_ALL=C sort -k2)"
    actual="$(LC_ALL=C sort -k2 <<<"${delivered}")"
    if [[ "${expected}" == "${actual}" ]]; then
        pass "all 8 delivered digests reproduce the accepted F0 baseline exactly"
    else
        # Not every difference is a content difference. The corresponding-source
        # archive is zstd-compressed, and two hosts with different zstd library
        # builds emit different compressed bytes for the identical tar stream.
        # Four further digests record that archive's digest and move with it.
        #
        # So before failing, check whether this is exactly that: the binaries and
        # the capability record identical, only the compressed container and the
        # four files that quote it differing, and the DECOMPRESSED stream equal to
        # the recorded accepted stream. That is a justified derivation of the
        # accepted runtime rather than a different runtime.
        #
        # It is still not silently acceptable. PKG_ALLOW_COMPRESSOR_DRIFT must be
        # set deliberately, and CI never sets it: a hosted build runs the same
        # runner image as the accepted run, so a difference there is a real one.
        # `|| true` on the diff, not on the pipeline: under `pipefail` a diff that
        # reports differences (exit 1) would otherwise abort the whole comparison
        # at exactly the moment it has something to say.
        moved="$( { diff <(echo "${expected}") <(echo "${actual}") || true; } \
                  | sed -nE 's/^> [0-9a-f]{64}  //p' | LC_ALL=C sort)"
        derived_ok=1
        for path in "${F0_RUNTIME_NAME}-${RID}/bin/ffmpeg" \
                    "${F0_RUNTIME_NAME}-${RID}/bin/ffprobe" \
                    "${F0_RUNTIME_NAME}-${RID}/capability.json"; do
            if grep -qxF "${path}" <<<"${moved}"; then derived_ok=0; fi
        done
        expected_stream="$(sed -nE "s/^# stream-sha256 ${F0_SOURCE_ARCHIVE} ([0-9a-f]{64})$/\1/p" "${PKG_F0_BASELINE}")"
        actual_stream="$(zstd -dc "${SRC_ARCHIVE}" | sha256sum | cut -d' ' -f1)"
        [[ -n "${expected_stream}" && "${expected_stream}" == "${actual_stream}" ]] || derived_ok=0

        if [[ "${derived_ok}" -eq 1 && "${PKG_ALLOW_COMPRESSOR_DRIFT:-0}" == "1" ]]; then
            pass "ffmpeg, ffprobe and capability.json reproduce the accepted baseline byte for byte"
            pass "corresponding source is content-identical to the accepted archive (decompressed stream ${actual_stream})"
            echo "  note: the compressed .tar.zst container and the four files quoting its digest"
            echo "        differ, which is a zstd-build difference between this host and the runner"
            echo "        that produced the baseline. Accepted here only because"
            echo "        PKG_ALLOW_COMPRESSOR_DRIFT=1 was set explicitly. Moved paths:"
            sed 's/^/          /' <<<"${moved}"
        elif [[ "${derived_ok}" -eq 1 ]]; then
            fail "delivered digests differ from the accepted baseline. The difference is confined to the compressed corresponding-source container and the four files quoting it (decompressed stream matches ${actual_stream}), but this build did not set PKG_ALLOW_COMPRESSOR_DRIFT=1, so it is treated as drift:"$'\n'"${moved}"
        else
            fail "delivered digests differ from the accepted baseline while ci/ffmpeg/** is unchanged:"$'\n'"$(diff <(echo "${expected}") <(echo "${actual}") || true)"
        fi
    fi
else
    # Not a pass and not a failure. ci/ffmpeg/** has moved, so the baseline no
    # longer describes these inputs; enforcing it would be enforcing a stale
    # oracle, and skipping quietly would hide that the comparison did not happen.
    echo "  note: ci/ffmpeg tree is ${baseline_state} relative to F0_ACCEPTED_CI_TREE;"
    echo "        the accepted digest baseline does not describe these inputs and was NOT enforced."
    echo "        Rebuilt digests for ${RID}:"
    sed 's/^/          /' <<<"${delivered}"
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "FFMPEG RUNTIME: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "FFMPEG RUNTIME: PASS — ${F0_BUILD_REVISION} ${RID}"
printf '%s\n' "${PKG}"
