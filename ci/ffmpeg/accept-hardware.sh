#!/usr/bin/env bash
# Runtime hardware acceptance for a built Tesserafin FFmpeg runtime (F0 / #229).
#
# Usage: ci/ffmpeg/accept-hardware.sh --runtime DIR [--evidence OUT.json]
#
# Separate from accept-runtime.sh on purpose. That script proves software
# behaviour on every declared environment and proves that the hardware surface
# QUERIES rather than aborts; it never claims a hardware path works, because
# hosted runners have no GPU and a claim without hardware is a lie with a
# timestamp on it.
#
# This script is the other half: it runs only where a real render node exists,
# performs a complete VAAPI transcode, and records what actually happened. If no
# render node is present it says so and exits 0 — absence of a GPU is a deferral,
# not a failure. What it will never do is write an affirmative claim it did not
# observe.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

RUNTIME=""; EVIDENCE=""; IN_IMAGE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --runtime)  RUNTIME="$2"; shift 2 ;;
        --evidence) EVIDENCE="$2"; shift 2 ;;
        --in-image) IN_IMAGE="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${RUNTIME}" ]] || ff_die "--runtime must be an existing runtime directory"

# A render node is owned by a 'render' group the invoking user is usually not a
# member of, and the VA driver itself (radeonsi/iHD) has to be present. Both are
# properties of the environment, not of the runtime under test, so --in-image
# re-runs this same script inside an image that has the driver, with the device
# passed through and the node's own GID added.
#
# This is the ONE place the build touches the network at run time, and it is not
# part of the reproducible build: it produces evidence, not artifacts.
if [[ -n "${IN_IMAGE}" ]]; then
    node="$(ls -1 /dev/dri/renderD* 2>/dev/null | head -1 || true)"
    [[ -n "${node}" ]] || ff_die "--in-image was given but this machine has no /dev/dri/renderD*"
    gid="$(stat -c %g "${node}")"
    ff_log "running hardware acceptance inside ${IN_IMAGE} (device ${node}, gid ${gid})"
    args=(--runtime /rt)
    [[ -n "${EVIDENCE}" ]] && args+=(--evidence "/evidence/$(basename "${EVIDENCE}")")
    docker run --rm \
        --device "${node}" \
        --group-add "${gid}" \
        --user "$(id -u):$(id -g)" \
        --env HOME=/tmp \
        --volume "${FF_REPO_ROOT}:/repo:ro" \
        --volume "${RUNTIME}:/rt:ro" \
        ${EVIDENCE:+--volume "$(cd "$(dirname "${EVIDENCE}")" && pwd):/evidence"} \
        "${IN_IMAGE}" \
        bash -c 'exec /repo/ci/ffmpeg/accept-hardware.sh '"${args[*]}"''
    exit $?
fi

FF="${RUNTIME}/bin/ffmpeg"
FP="${RUNTIME}/bin/ffprobe"
[[ -x "${FF}" && -x "${FP}" ]] || ff_die "no ffmpeg/ffprobe under ${RUNTIME}/bin"

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

STATUS="deferred"
DETAIL="no render node present"
DEVICE=""
DRIVER=""
ENCODER_LINE=""

shopt -s nullglob
NODES=(/dev/dri/renderD*)
shopt -u nullglob

if [[ "${#NODES[@]}" -eq 0 ]]; then
    echo "== no /dev/dri/renderD* on this machine"
    echo "   VAAPI runtime acceptance is DEFERRED. Compiled capability is recorded"
    echo "   separately by verify-runtime.sh and makes no runtime claim."
else
    DEVICE="${NODES[0]}"
    echo "== render node ${DEVICE}"

    # Readability is not access: the node is typically owned by a 'render' group.
    if [[ ! -r "${DEVICE}" ]]; then
        DETAIL="render node ${DEVICE} exists but is not readable by $(id -un) (groups: $(id -Gn))"
        echo "   ${DETAIL}"
        echo "   VAAPI runtime acceptance is DEFERRED."
    else
        # A real transcode, not a device probe: decode a generated H.264 file and
        # re-encode it through h264_vaapi, then prove the OUTPUT is genuinely
        # H.264 and genuinely non-empty. A device that initialises and then
        # produces nothing is not a working hardware path.
        "${FF}" -hide_banner -loglevel error \
            -f lavfi -i testsrc=size=640x360:rate=25:duration=3 \
            -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
            -f mp4 "${WORK}/src.mp4" -y

        set +e
        "${FF}" -hide_banner -loglevel verbose \
            -init_hw_device "vaapi=va:${DEVICE}" -filter_hw_device va \
            -i "${WORK}/src.mp4" \
            -vf 'format=nv12,hwupload' \
            -c:v h264_vaapi -f mp4 "${WORK}/out.mp4" -y \
            > "${WORK}/vaapi.log" 2>&1
        rc=$?
        set -e

        # Exit code alone cannot tell a crash from a controlled failure here:
        # ffmpeg reports AVERROR values by truncating a negative errno into the
        # exit status, so EIO leaves 251 and EINVAL leaves 234 — squarely inside
        # the range a signal death would occupy. The abort signature on stderr
        # is what actually separates them.
        if grep -qE 'Assertion|assertion failed|Segmentation fault|stack smashing|core dumped' \
                "${WORK}/vaapi.log"; then
            # The specific failure #229 exists to eliminate.
            STATUS="failed"
            DETAIL="the VAAPI transcode crashed: $(grep -m1 -E 'Assertion|Segmentation fault' "${WORK}/vaapi.log" | tail -1)"
            echo "   FAIL: ${DETAIL}"
            tail -5 "${WORK}/vaapi.log" >&2
        elif [[ "${rc}" -ne 0 ]]; then
            STATUS="unavailable"
            DETAIL="the VAAPI transcode failed in a controlled way: $(tail -1 "${WORK}/vaapi.log")"
            echo "   ${DETAIL}"
        elif [[ ! -s "${WORK}/out.mp4" ]]; then
            STATUS="failed"
            DETAIL="the VAAPI transcode reported success but produced no output"
            echo "   FAIL: ${DETAIL}"
        else
            codec="$("${FP}" -hide_banner -loglevel error -select_streams v:0 \
                        -show_entries stream=codec_name -of default=nw=1:nk=1 "${WORK}/out.mp4")"
            bytes="$(stat -c %s "${WORK}/out.mp4")"
            if [[ "${codec}" != "h264" ]]; then
                STATUS="failed"
                DETAIL="the VAAPI output is '${codec}', not h264"
                echo "   FAIL: ${DETAIL}"
            else
                STATUS="proven"
                DRIVER="$(grep -oiE 'Driver [Vv]ersion: .*' "${WORK}/vaapi.log" | head -1 || true)"
                ENCODER_LINE="$(grep -oE 'Using device [^ ]+|VAAPI [^\n]*' "${WORK}/vaapi.log" | head -1 || true)"
                DETAIL="h264_vaapi produced ${bytes} bytes of h264 on ${DEVICE}"
                echo "   ok  : ${DETAIL}"
                [[ -n "${DRIVER}" ]] && echo "   ok  : ${DRIVER}"
            fi
        fi
    fi
fi

if [[ -n "${EVIDENCE}" ]]; then
    python3 - "${EVIDENCE}" "${STATUS}" "${DETAIL}" "${DEVICE}" "${DRIVER}" "${ENCODER_LINE}" \
             "$(uname -m)" "$(uname -r)" <<'PY'
import json, sys
dest, status, detail, device, driver, encoder, machine, kernel = sys.argv[1:9]
record = {
    "$comment": ("Runtime hardware evidence. 'proven' is written only after a complete "
                 "transcode produced verified output on a real render node. 'deferred' "
                 "means no hardware was present; it is not a claim of any kind."),
    "vaapi": {
        "status": status,
        "detail": detail,
        "device": device or None,
        "driver": driver or None,
        "observed": encoder or None,
    },
    "qsv":   {"status": "deferred", "detail": "no Intel GPU on the machine that ran this"},
    "nvenc": {"status": "deferred", "detail": "no NVIDIA GPU on the machine that ran this"},
    "amf":   {"status": "deferred", "detail": "AMF requires the proprietary AMD runtime, which is not installed"},
    "host": {"machine": machine, "kernel": kernel},
}
with open(dest, "w") as h:
    json.dump(record, h, indent=2, sort_keys=True)
    h.write("\n")
print(f"   evidence written to {dest}")
PY
fi

# An abort or a false claim fails. Absence of hardware does not.
[[ "${STATUS}" != "failed" ]] || exit 1
echo "HARDWARE: ${STATUS}"
