#!/usr/bin/env bash
# VAAPI package-integration acceptance (#225 / [L0]).
#
# Usage: ci/package/accept-vaapi.sh --artifacts DIR --rid RID [--evidence OUT.json]
#
# Runs only where a real AMD/Intel render node exists. It proves that the FFmpeg
# runtime INSIDE A REAL PACKAGE ARTIFACT performs a complete hardware transcode
# from its installed location — not that the staging tree can, and not that the
# server selects hardware automatically.
#
# The distinction matters. A staging tree is the build's own directory, with the
# build's own paths and whatever the build happened to leave lying beside it. A
# package is what a user receives. The bundled libva layout, the $ORIGIN RUNPATH
# and the absence of a host fallback are all properties of the INSTALLED tree, so
# they are tested there: the .deb is unpacked and its own
# usr/lib/tesserafin/ffmpeg/bin/ffmpeg is the binary invoked.
#
# The transcode itself is delegated to ci/ffmpeg/accept-hardware.sh — the F0
# script that already knows how to tell a crash from a controlled failure and
# refuses to write an affirmative claim it did not observe. This script adds the
# checks that are about the PACKAGE:
#
#   * the output is genuinely H.264, with the expected frame count and duration,
#     and decodes back cleanly;
#   * every library the encoder loads comes from inside the unpacked package;
#   * no system ffmpeg and no system libva was substituted;
#   * no abort, and no silent fall back to a software encoder.
#
# Absence of a render node is a DEFERRAL, not a failure: this script says so and
# exits 0. What it will never do is record a hardware claim it did not observe.
#
# What this does NOT prove: that the server chooses hardware on its own. That is
# #29 / #76 and neither is complete.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ARTIFACTS=""; RID=""; EVIDENCE=""; IN_IMAGE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --artifacts) ARTIFACTS="$2"; shift 2 ;;
        --rid)       RID="$2"; shift 2 ;;
        --evidence)  EVIDENCE="$2"; shift 2 ;;
        --in-image)  IN_IMAGE="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -d "${ARTIFACTS}" ]] || pkg_die "--artifacts must be an existing artifact directory"
[[ -n "${RID}" ]]       || pkg_die "--rid is required"

pkg_load_pins
pkg_load_version_contract

DEB="${ARTIFACTS}/tesserafin-server_${VERSION}-1_$(pkg_deb_arch "${RID}").deb"
[[ -f "${DEB}" ]] || pkg_die "no .deb artifact at ${DEB}"

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

echo "== unpacking the real package artifact"
echo "   $(basename "${DEB}") ($(pkg_sha256 "${DEB}"))"
dpkg-deb -x "${DEB}" "${WORK}/root"
RT="${WORK}/root/usr/lib/tesserafin/ffmpeg"
FF="${RT}/bin/ffmpeg"
FP="${RT}/bin/ffprobe"
[[ -x "${FF}" && -x "${FP}" ]] || pkg_die "the unpacked package has no executable ffmpeg/ffprobe"

shopt -s nullglob
NODES=(/dev/dri/renderD*)
shopt -u nullglob
if [[ "${#NODES[@]}" -eq 0 ]]; then
    echo "== no /dev/dri/renderD* on this machine"
    echo "   VAAPI package integration is DEFERRED. Compiled capability is recorded"
    echo "   separately and makes no runtime claim."
    exit 0
fi
DEVICE="${NODES[0]}"

# The render node is owned by a group the invoking user is usually not in, and
# the VA driver (radeonsi/iHD) has to be present. Both are properties of the
# environment, not of the package, so --in-image re-runs this script inside an
# image that has the driver, with the device passed through and its GID added.
if [[ -n "${IN_IMAGE}" ]]; then
    gid="$(stat -c %g "${DEVICE}")"
    pkg_log "running package VAAPI integration inside ${IN_IMAGE} (device ${DEVICE}, gid ${gid})"
    args=(--artifacts /artifacts --rid "${RID}")
    [[ -n "${EVIDENCE}" ]] && args+=(--evidence "/evidence/$(basename "${EVIDENCE}")")
    docker run --rm \
        --device "${DEVICE}" \
        --group-add "${gid}" \
        --user "$(id -u):$(id -g)" \
        --env HOME=/tmp \
        --env PKG_ALLOW_COMPRESSOR_DRIFT="${PKG_ALLOW_COMPRESSOR_DRIFT:-0}" \
        `# The container sees /repo as a read-only bind mount with no git` \
        `# metadata, so docker/version-contract.sh cannot run there. The outer` \
        `# invocation, which IS in a checkout, resolves it once and hands the` \
        `# result down — the same mechanism the lifecycle scripts use.` \
        --env PKG_VERSION="${VERSION}" \
        --env PKG_VCS_REF="${VCS_REF}" \
        --env PKG_SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH}" \
        --volume "${PKG_REPO_ROOT}:/repo:ro" \
        --volume "${ARTIFACTS}:/artifacts:ro" \
        ${EVIDENCE:+--volume "$(cd "$(dirname "${EVIDENCE}")" && pwd):/evidence"} \
        "${IN_IMAGE}" \
        bash -c 'exec /repo/ci/package/accept-vaapi.sh '"${args[*]}"''
    exit $?
fi

FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

echo "== the packaged encoder is the one being invoked"
pass "binary: ${FF#"${WORK}/root"} (from the .deb, not the staging tree)"
pass "$("${FF}" -hide_banner -version | head -1)"

# No system substitution. If a host ffmpeg or a host libva were in play, these
# would point outside the unpacked package.
outside=""
while read -r soname _ resolved _; do
    [[ "${resolved}" == /* ]] || continue
    case "${resolved}" in
        "${WORK}/root"/*) : ;;
        *) [[ -e "${RT}/lib/${soname}" ]] && outside+="${soname} -> ${resolved}"$'\n' ;;
    esac
done < <(ldd "${FF}" 2>/dev/null | sed -nE 's/^\s*(\S+) (=>) (\S+).*/\1 \2 \3 x/p')
if [[ -n "${outside}" ]]; then
    fail "a bundled library resolved to a HOST copy:"$'\n'"${outside}"
else
    pass "every bundled library resolves inside the unpacked package"
fi
# libva is a DT_NEEDED of ffmpeg, so it is resolved by the loader rather than
# dlopen'd, and it must come from the package. The loader reports the path it
# actually used in $ORIGIN-relative form — `.../ffmpeg/bin/../lib/libva.so.2` —
# so both sides are canonicalised before comparing; matching the literal
# canonical string against the loader's output silently never matches.
for soname in libva.so.2 libva-drm.so.2; do
    resolved="$(ldd "${FF}" 2>/dev/null | sed -nE "s|^\s*${soname} => (\S+).*|\1|p" | head -1)"
    if [[ -z "${resolved}" ]]; then
        fail "${soname} is not among the libraries the loader resolves for ffmpeg"
    elif [[ "$(readlink -f "${resolved}")" == "$(readlink -f "${RT}/lib/${soname}")" ]]; then
        pass "${soname} comes from the package's own lib/ (loader used ${resolved#"${WORK}/root"})"
    else
        fail "${soname} resolved to ${resolved}, not the package's bundled copy"
    fi
done

# The loop above can only find what ldd printed. If ldd produced nothing the
# checks would pass vacuously, so the output is required to be non-trivial.
ldd_lines="$(ldd "${FF}" 2>/dev/null | wc -l)"
if [[ "${ldd_lines}" -ge 5 ]]; then
    pass "the loader reports ${ldd_lines} entries for ffmpeg (the library checks are not vacuous)"
else
    fail "ldd produced only ${ldd_lines} line(s); the library-resolution checks cannot be trusted"
fi

# =============================================================================
# The transcode, delegated to the F0 script that already classifies failures
# =============================================================================
echo "== VAAPI transcode from the packaged runtime"
HW_EVIDENCE="${WORK}/hardware.json"
if ! "${PKG_REPO_ROOT}/ci/ffmpeg/accept-hardware.sh" --runtime "${RT}" --evidence "${HW_EVIDENCE}"; then
    fail "ci/ffmpeg/accept-hardware.sh reported a failure against the packaged runtime"
fi
# The F0 record nests per-path status: {"vaapi": {"status": ...}, "qsv": {...}}.
hw_status="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["vaapi"]["status"])' "${HW_EVIDENCE}" 2>/dev/null || echo unknown)"
case "${hw_status}" in
    proven)   pass "F0 hardware acceptance: proven, from the packaged binary" ;;
    deferred) echo "  note: F0 hardware acceptance deferred (${hw_status}); no claim recorded"; exit 0 ;;
    *)        fail "F0 hardware acceptance status is '${hw_status}', not 'proven'" ;;
esac

# =============================================================================
# The package-level output checks
# =============================================================================
#
# "It produced h264" is where the F0 script stops, because that is all F0 needs.
# A package claim needs more: a file can be h264-tagged, non-empty, and still be
# a truncated stream that no player will accept. So the output is measured
# against the input it was made from, and then decoded back.
echo "== the produced stream is a complete, decodable H.264 transcode"
SRC="${WORK}/src.mp4"; OUT="${WORK}/out.mp4"; LOG="${WORK}/vaapi.log"

"${FF}" -hide_banner -loglevel error \
    -f lavfi -i testsrc=size=640x360:rate=25:duration=4 \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p -f mp4 "${SRC}" -y

set +e
"${FF}" -hide_banner -loglevel verbose \
    -init_hw_device "vaapi=va:${DEVICE}" -filter_hw_device va \
    -i "${SRC}" -vf 'format=nv12,hwupload' \
    -c:v h264_vaapi -f mp4 "${OUT}" -y > "${LOG}" 2>&1
rc=$?
set -e

if grep -qE 'Assertion|assertion failed|Segmentation fault|stack smashing|core dumped' "${LOG}"; then
    fail "the transcode aborted: $(grep -m1 -E 'Assertion|Segmentation fault' "${LOG}")"
elif [[ "${rc}" -ne 0 ]]; then
    fail "the transcode failed (rc=${rc}): $(tail -1 "${LOG}")"
else
    pass "no abort, no crash signature (rc=0)"

    # The encoder that actually ran. A build can silently fall back to libx264 if
    # the hardware encoder cannot be opened, and the output would still be h264 —
    # which is exactly why "the output is h264" is not sufficient evidence.
# The encoder that actually ran is read from the stream MAPPING line, which
    # names the input codec on the left and the output encoder on the right:
    #
    #   Stream #0:0 -> #0:0 (h264 (native) -> h264 (h264_vaapi))
    #
    # Grepping the whole log for "libx264" would always match here: the fixture
    # was produced with libx264 by this same binary, so the input file carries a
    # libx264 encoder METADATA tag. That tag says nothing about which encoder
    # produced the output, and treating it as evidence would fail every run.
    mapping="$(grep -oE 'Stream #[0-9]+:[0-9]+ -> #[0-9]+:[0-9]+ \([^)]*\([^)]*\)[^)]*\)' "${LOG}" | head -1)"
    output_side="${mapping##*-> }"
    if [[ "${output_side}" == *h264_vaapi* ]]; then
        pass "the output encoder is h264_vaapi (${mapping:-mapping line not printed})"
    else
        fail "the output encoder is not h264_vaapi; mapping was '${mapping:-<none>}'"
    fi
    if [[ "${output_side}" == *libx264* || "${output_side}" == *"(native)"* ]]; then
        fail "a software encoder was substituted on the output side: ${output_side}"
    else
        pass "no software encoder substitution on the output side"
    fi

    read -r codec frames duration <<<"$("${FP}" -hide_banner -loglevel error \
        -select_streams v:0 -count_frames \
        -show_entries stream=codec_name,nb_read_frames:format=duration \
        -of default=nw=1:nk=1 "${OUT}" | tr '\n' ' ')"
    read -r src_codec src_frames src_duration <<<"$("${FP}" -hide_banner -loglevel error \
        -select_streams v:0 -count_frames \
        -show_entries stream=codec_name,nb_read_frames:format=duration \
        -of default=nw=1:nk=1 "${SRC}" | tr '\n' ' ')"

    [[ "${codec}" == "h264" ]] \
        && pass "output codec is h264 (input was ${src_codec})" \
        || fail "output codec is '${codec}', not h264"
    [[ "${frames}" == "${src_frames}" ]] \
        && pass "frame count preserved: ${frames} frames in, ${frames} out" \
        || fail "frame count changed: ${src_frames} in, ${frames} out"
    if python3 -c "import sys;sys.exit(0 if abs(float('${duration}')-float('${src_duration}'))<=0.15 else 1)"; then
        pass "duration preserved: ${src_duration}s in, ${duration}s out"
    else
        fail "duration changed: ${src_duration}s in, ${duration}s out"
    fi

    # Decodability: the stream is decoded end to end and every frame has to come
    # back out. A truncated or malformed stream fails here even when the
    # container metadata looks correct.
    set +e
    decoded="$("${FF}" -hide_banner -loglevel error -i "${OUT}" -f null - 2>&1)"
    drc=$?
    set -e
    if [[ "${drc}" -eq 0 && -z "${decoded}" ]]; then
        pass "the output decodes back cleanly, end to end, with no errors"
    else
        fail "the output does not decode cleanly: rc=${drc} ${decoded}"
    fi
fi

if [[ -n "${EVIDENCE}" ]]; then
    python3 - "${EVIDENCE}" "${HW_EVIDENCE}" "${DEB}" "$(pkg_sha256 "${DEB}")" \
             "${RID}" "${F0_BUILD_REVISION}" "${DEVICE}" "${FAILURES}" <<'PY'
import json, os, sys
dest, hw_path, deb, deb_sha, rid, revision, device, failures = sys.argv[1:9]
hw = json.load(open(hw_path)) if os.path.exists(hw_path) else {}
json.dump({
    "$comment": ("Package integration evidence. It records that the FFmpeg runtime inside a "
                 "real package artifact performed a complete hardware transcode from its "
                 "installed location. It is NOT evidence that the server selects hardware "
                 "automatically; see #29 and #76, neither of which this completes."),
    "artifact": os.path.basename(deb),
    "artifactSha256": deb_sha,
    "runtimeIdentifier": rid,
    "ffmpegBuildRevision": revision,
    "renderNode": device,
    "status": "proven" if failures == "0" else "failed",
    "f0HardwareEvidence": hw,
}, open(dest, "w"), indent=2, sort_keys=True)
open(dest, "a").write("\n")
PY
    echo "  evidence: ${EVIDENCE}"
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "VAAPI PACKAGE INTEGRATION: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "VAAPI PACKAGE INTEGRATION: PASS — ${RID}, ${DEVICE}"
