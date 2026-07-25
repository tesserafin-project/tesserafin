#!/usr/bin/env bash
# A4 real-VAAPI acceptance gate (#90): the opt-in hardware path, on real hardware.
#
# Requires an actual VAAPI-capable GPU. Nothing here is mocked or simulated: if
# no render node is present the script says so and exits 2, because a simulated
# pass would be worth less than no result at all.
#
# It proves, against a running production container:
#   1. the render device is visible and usable by the non-root container user;
#   2. the real startup trial encode succeeds and selects VAAPI;
#   3. a real item transcodes through h264_vaapi and returns media bytes;
#   4. media stays read-only;
#   5. a restart RE-PROBES rather than trusting the stored selection;
#   6. removing the device from that same persisted state falls back to software
#      and still completes a real transcode.
#
# Step 6 is the load-bearing one. It is the GPU-host-to-no-GPU-host migration
# that the old persist-and-never-recheck behaviour got wrong.
#
# The startup trial encode is deliberately NOT counted as the media transcode:
# it proves the device works, not that the transcoding pipeline uses it.
#
# Usage: docker/hwa-vaapi.sh <image-ref> [host-port] [render-device]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=docker/hwa-lib.sh
source "${REPO_ROOT}/docker/hwa-lib.sh"

IMAGE="${1:?usage: hwa-vaapi.sh <image-ref> [host-port] [render-device]}"
PORT="${2:-18296}"
DEVICE="${3:-/dev/dri/renderD128}"

WORK="$(mktemp -d)"
CNAME="tesserafin-hwa-vaapi-$$"

cleanup() {
  docker rm -f "${CNAME}" >/dev/null 2>&1 || true
  docker run --rm -v "${WORK}:/w" "${HWA_HELPER}" chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}"
}
trap cleanup EXIT

echo "== 0. read-only hardware discovery =="
if [[ ! -e "${DEVICE}" ]]; then
  echo "  SKIP  no render node at ${DEVICE} — this gate needs real VAAPI hardware."
  echo "HWA VAAPI GATE: NOT RUN — no hardware available"
  exit 2
fi
RENDER_GID="$(stat -c '%g' "${DEVICE}")"
info "device      : ${DEVICE}"
info "ownership   : $(stat -c '%U:%G %a' "${DEVICE}") (numeric gid ${RENDER_GID})"
if command -v lspci >/dev/null 2>&1; then
  info "gpu         : $(lspci -nn 2>/dev/null | grep -iE 'vga|display|3d' | head -1)"
fi

# Can the pinned ffmpeg in THIS image drive the device as the non-root user?
VAINFO="$(docker run --rm --device "${DEVICE}" --group-add "${RENDER_GID}" \
            --entrypoint /usr/lib/jellyfin-ffmpeg/vainfo "${IMAGE}" \
            --display drm --device "${DEVICE}" 2>&1 || true)"
info "driver      : $(grep -m1 'Driver version' <<<"${VAINFO}" || echo 'unavailable')"
if grep -q 'VAProfileH264.*VAEntrypointEncSlice' <<<"${VAINFO}"; then
  pass "device exposes an H.264 encode entrypoint to the container"
else
  fail "device does not expose an H.264 encode entrypoint (VAAPI transcoding cannot work here)"
  echo "${VAINFO}" | tail -20
  exit 1
fi

echo "== 1. prepare state and fixture =="
mkdir -p "${WORK}/config" "${WORK}/cache" "${WORK}/data" "${WORK}/media"
hwa_make_fixture "${IMAGE}" "${WORK}/media"
FIXTURE="${WORK}/media/Sample Movie (2021)/Sample Movie (2021).mkv"
[[ -s "${FIXTURE}" ]] && pass "MPEG-2/MP2 fixture created" || { fail "fixture was not created"; exit 1; }
FIXTURE_SUM="$(sha256sum "${FIXTURE}" | cut -d' ' -f1)"
docker run --rm -v "${WORK}:/w" "${HWA_HELPER}" chown -R 10000:10000 /w/config /w/cache /w/data

# Mirrors docker-compose.vaapi.yml exactly: one render node, the host render gid
# as a supplementary group, non-root, no-new-privileges. No privileged mode, no
# Docker socket, no host networking, media read-only.
run_with_device() {
  docker run -d --name "${CNAME}" \
    -p "127.0.0.1:${PORT}:8096" \
    --device "${DEVICE}:${DEVICE}:rw" \
    --group-add "${RENDER_GID}" \
    --security-opt no-new-privileges:true \
    -v "${WORK}/config:/config" -v "${WORK}/cache:/cache" -v "${WORK}/data:/data" \
    -v "${WORK}/media:/media:ro" \
    "${IMAGE}" >/dev/null
}

run_without_device() {
  docker run -d --name "${CNAME}" \
    -p "127.0.0.1:${PORT}:8096" \
    --security-opt no-new-privileges:true \
    -v "${WORK}/config:/config" -v "${WORK}/cache:/cache" -v "${WORK}/data:/data" \
    -v "${WORK}/media:/media:ro" \
    "${IMAGE}" >/dev/null
}

echo "== 2. first boot with the render device =="
run_with_device
wait_ready "${PORT}" || { fail "container never became ready"; docker logs "${CNAME}" 2>&1 | tail -40; exit 1; }
pass "container booted with the render device mapped"

DEV_UID="$(docker exec "${CNAME}" id -u)"
[[ "${DEV_UID}" == "10000" ]] && pass "running as non-root uid 10000" || fail "uid is ${DEV_UID}"
if docker exec "${CNAME}" sh -c "test -r ${DEVICE} && test -w ${DEVICE}"; then
  pass "uid 10000 can read and write ${DEVICE} via the added render group"
else
  fail "uid 10000 cannot access ${DEVICE}"
fi

echo "== 3. the startup trial encode selected VAAPI =="
# Captured first, then matched from a herestring: `docker logs ... | grep -q` reports
# failure even on a match under `set -o pipefail`, because grep exits on the match and
# the writer takes SIGPIPE (141). Latent since always, reachable now that JSON-lines
# logging (#91 / [A5]) makes the log outgrow the pipe buffer.
PROBE_LOG="$(docker logs "${CNAME}" 2>&1)"
grep -F 'Hardware backend probe succeeded' <<<"${PROBE_LOG}" | grep -q 'h264_vaapi' \
  && pass "the real VAAPI startup trial encode succeeded" \
  || fail "no successful h264_vaapi trial encode in the startup log"
hwa_assert_decision "${CNAME}" "hardware" "vaapi" "PreferredBackendVerified|AutoSelectedBackendVerified"

echo "== 4. force a real transcode through the hardware path =="
read -r TOKEN USERID <<<"$(hwa_onboard "${PORT}")"
[[ -n "${TOKEN:-}" ]] && pass "admin created and authenticated" || { fail "onboarding/auth failed"; exit 1; }
ITEMID="$(hwa_library_item "${PORT}" "${TOKEN}" "${USERID}")"
[[ -n "${ITEMID}" ]] && pass "library scan found the fixture (item ${ITEMID})" || { fail "scan produced no item"; exit 1; }

RESULT="$(hwa_transcode "${PORT}" "${TOKEN}" "${ITEMID}" "${WORK}/vaapi.mp4")"
info "${RESULT}"
HTTP_CODE="${RESULT#http=}"; HTTP_CODE="${HTTP_CODE%% *}"
BYTES="${RESULT##*bytes=}"
[[ "${HTTP_CODE}" == "200" ]] && pass "playback request returned HTTP 200" || fail "playback request returned HTTP ${HTTP_CODE}"
[[ "${BYTES}" -gt 100000 ]] && pass "received ${BYTES} bytes of media" || fail "received only ${BYTES} bytes"

PROBE="$(docker run --rm --user 0:0 --entrypoint /usr/lib/jellyfin-ffmpeg/ffprobe \
          -v "${WORK}:/w" "${IMAGE}" -v error -select_streams v:0 \
          -show_entries stream=codec_name,width,height -of default=nw=1 /w/vaapi.mp4 2>&1 || true)"
info "ffprobe: $(echo "${PROBE}" | tr '\n' ' ')"
grep -q 'codec_name=h264' <<<"${PROBE}" && pass "returned bytes probe as H.264 video" || fail "returned bytes are not probeable H.264"

FFCMD="$(hwa_ffmpeg_command "${CNAME}")"
if [[ -z "${FFCMD}" ]]; then
  fail "no assembled ffmpeg command found in the container log"
else
  info "$(echo "${FFCMD}" | grep -oE '\-(codec|c):v:0 [a-z0-9_]+')"
  grep -qE '\-(codec|c):v:0 h264_vaapi' <<<"${FFCMD}" \
    && pass "the media transcode used h264_vaapi" \
    || fail "the media transcode did not use h264_vaapi"
  grep -q 'vaapi_device\|hwupload\|init_hw_device vaapi' <<<"${FFCMD}" \
    && pass "the command uses the VAAPI device/filter path" \
    || fail "the command does not initialise a VAAPI device"
  # #76 stays out of scope and mjpeg_vaapi stays off the transcode path.
  grep -q 'mjpeg_vaapi' <<<"${FFCMD}" \
    && fail "mjpeg_vaapi appeared in a transcode command" \
    || pass "mjpeg_vaapi was not used for transcoding"
fi

echo "== 5. media stayed read-only =="
if docker exec "${CNAME}" sh -c 'touch /media/.should-fail' 2>/dev/null; then
  fail "read-only media mount is writable"
else
  pass "media mount is still read-only"
fi
[[ "$(sha256sum "${FIXTURE}" | cut -d' ' -f1)" == "${FIXTURE_SUM}" ]] \
  && pass "source media file is byte-identical after hardware transcoding" \
  || fail "source media file changed"

echo "== 6. restart re-probes instead of trusting the stored selection =="
docker rm -f "${CNAME}" >/dev/null
run_with_device
wait_ready "${PORT}" || { fail "restart never became ready"; exit 1; }
# The selection persisted as vaapi. A restart that trusted it would show no probe.
RESTART_LOG="$(docker logs "${CNAME}" 2>&1)"
grep -F 'Hardware backend probe succeeded' <<<"${RESTART_LOG}" | grep -q 'h264_vaapi' \
  && pass "restart ran the VAAPI trial encode again rather than trusting stored state" \
  || fail "restart did not re-probe VAAPI"
hwa_assert_decision "${CNAME}" "hardware" "vaapi" "PreferredBackendVerified|AutoSelectedBackendVerified"

echo "== 7. device removed, SAME persisted state: software and a real transcode =="
# The migration case. The stored configuration still names vaapi; the device is
# gone. This must select software and keep transcoding, not fail and not emit a
# VAAPI command line.
docker rm -f "${CNAME}" >/dev/null
run_without_device
wait_ready "${PORT}" || { fail "container without the device never became ready"; docker logs "${CNAME}" 2>&1 | tail -40; exit 1; }
pass "container booted from GPU-era state with no device present"
hwa_assert_decision "${CNAME}" "software" "none" "NoApplicableBackend|AllProbesFailed"

# Same admin, same token, same item: the state volume is the one from the VAAPI
# run, so no re-onboarding is needed — which is itself part of what is proven.
RESULT2="$(hwa_transcode "${PORT}" "${TOKEN}" "${ITEMID}" "${WORK}/fallback.mp4")"
info "${RESULT2}"
HTTP2="${RESULT2#http=}"; HTTP2="${HTTP2%% *}"
BYTES2="${RESULT2##*bytes=}"
[[ "${HTTP2}" == "200" ]] && pass "playback request returned HTTP 200 after device removal" || fail "playback returned HTTP ${HTTP2}"
[[ "${BYTES2}" -gt 100000 ]] && pass "received ${BYTES2} bytes of media after device removal" || fail "received only ${BYTES2} bytes"

FFCMD2="$(hwa_ffmpeg_command "${CNAME}")"
if [[ -z "${FFCMD2}" ]]; then
  fail "no assembled ffmpeg command found after device removal"
else
  info "$(echo "${FFCMD2}" | grep -oE '\-(codec|c):v:0 [a-z0-9_]+')"
  grep -qE '\-(codec|c):v:0 libx264' <<<"${FFCMD2}" \
    && pass "post-removal transcode used the software encoder libx264" \
    || fail "post-removal transcode did not use libx264"
  if grep -qE "\-(codec|c):v:0 (${HWA_HW_ENCODERS})" <<<"${FFCMD2}"; then
    fail "a hardware encoder was used after the device was removed"
  else
    pass "no hardware encoder was used after the device was removed"
  fi
fi

echo
if [[ "${FAILED}" == 0 ]]; then
  echo "HWA VAAPI GATE: all gates passed"
else
  echo "HWA VAAPI GATE: FAILURES present"
  exit 1
fi
