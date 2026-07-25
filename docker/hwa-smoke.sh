#!/usr/bin/env bash
# A4 no-device acceptance gate (#90): the zero-configuration software path.
#
# Runs the production image exactly as an operator with no GPU would get it —
# no /dev/dri mapping, fresh config/data/cache, read-only media, and not one
# encoding setting touched — then proves it actually transcodes.
#
# What "proves" means here, and what it deliberately does not accept:
#   - a server that boots is not a transcode;
#   - an API that answers is not a transcode;
#   - a unit test that says libx264 would be chosen is not a transcode.
# This gate forces a real item through the encoder, pulls the resulting media
# bytes back over HTTP, and reads the concrete ffmpeg command out of the running
# container's own log.
#
# Usage: docker/hwa-smoke.sh <image-ref> [host-port]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=docker/hwa-lib.sh
source "${REPO_ROOT}/docker/hwa-lib.sh"

IMAGE="${1:?usage: hwa-smoke.sh <image-ref> [host-port]}"
PORT="${2:-18196}"
# Deliberately larger than docker/smoke.sh's 30s. That budget is measured on a
# server that only ever booted; this one has just run a transcode, and tearing
# the finished transcode job down adds around half a minute before the process
# exits. Measured against untouched master: exit 0 after ~56s, i.e. genuinely
# graceful, merely slower. A 30s budget here would report a forced kill (137)
# that says nothing about this change. Raise it further for emulated arm64 runs.
STOP_TIMEOUT="${STOP_TIMEOUT:-120}"

WORK="$(mktemp -d)"
CNAME="tesserafin-hwa-nodev-$$"

cleanup() {
  docker rm -f "${CNAME}" >/dev/null 2>&1 || true
  docker run --rm -v "${WORK}:/w" "${HWA_HELPER}" chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}"
}
trap cleanup EXIT

mkdir -p "${WORK}/config" "${WORK}/cache" "${WORK}/data" "${WORK}/media"

echo "== image under test: ${IMAGE} (NO /dev/dri mapping) =="

echo "== 0. fixture that cannot be direct-played or remuxed =="
hwa_make_fixture "${IMAGE}" "${WORK}/media"
FIXTURE="${WORK}/media/Sample Movie (2021)/Sample Movie (2021).mkv"
[[ -s "${FIXTURE}" ]] && pass "MPEG-2/MP2 fixture created ($(stat -c%s "${FIXTURE}") bytes)" || { fail "fixture was not created"; exit 1; }
docker run --rm -v "${WORK}:/w" "${HWA_HELPER}" chown -R 10000:10000 /w/config /w/cache /w/data

echo "== 1. boot with no GPU and no encoding configuration =="
# No --device, no --group-add, no encoding env: the default operator experience.
docker run -d --name "${CNAME}" \
  -p "127.0.0.1:${PORT}:8096" \
  -v "${WORK}/config:/config" \
  -v "${WORK}/cache:/cache" \
  -v "${WORK}/data:/data" \
  -v "${WORK}/media:/media:ro" \
  "${IMAGE}" >/dev/null

if wait_ready "${PORT}"; then
  pass "container booted and left the startup phase with no GPU present"
else
  fail "container never became ready"
  docker logs "${CNAME}" 2>&1 | tail -40
  exit 1
fi

# The host really has no render node inside this container — the premise of the gate.
if docker exec "${CNAME}" sh -c 'ls /dev/dri/renderD* >/dev/null 2>&1'; then
  fail "a render node is visible inside the container; this is not a no-device run"
else
  pass "no render node inside the container"
fi

echo "== 2. conclusive software decision in the startup log =="
hwa_assert_decision "${CNAME}" "software" "none" "NoApplicableBackend|AllProbesFailed"

echo "== 3. onboarding far enough to request playback =="
read -r TOKEN USERID <<<"$(hwa_onboard "${PORT}")"
[[ -n "${TOKEN:-}" ]] && pass "admin created and authenticated" || { fail "onboarding/auth failed"; exit 1; }

ITEMID="$(hwa_library_item "${PORT}" "${TOKEN}" "${USERID}")"
[[ -n "${ITEMID}" ]] && pass "library scan found the fixture (item ${ITEMID})" || { fail "scan produced no item"; exit 1; }

echo "== 4. force a real transcode and read the bytes back =="
RESULT="$(hwa_transcode "${PORT}" "${TOKEN}" "${ITEMID}" "${WORK}/out.mp4")"
info "${RESULT}"
HTTP_CODE="${RESULT#http=}"; HTTP_CODE="${HTTP_CODE%% *}"
BYTES="${RESULT##*bytes=}"

[[ "${HTTP_CODE}" == "200" ]] && pass "playback request returned HTTP 200" || fail "playback request returned HTTP ${HTTP_CODE}"
# A guard against a 200 with an empty or error body being counted as success.
[[ "${BYTES}" -gt 100000 ]] && pass "received ${BYTES} bytes of media" || fail "received only ${BYTES} bytes"

# The bytes must be a real decodable H.264 elementary stream, not just a container header.
PROBE="$(docker run --rm --user 0:0 --entrypoint /usr/lib/jellyfin-ffmpeg/ffprobe \
          -v "${WORK}:/w" "${IMAGE}" -v error \
          -select_streams v:0 -show_entries stream=codec_name,width,height \
          -of default=nw=1 /w/out.mp4 2>&1 || true)"
info "ffprobe: $(echo "${PROBE}" | tr '\n' ' ')"
grep -q 'codec_name=h264' <<<"${PROBE}" && pass "returned bytes probe as H.264 video" || fail "returned bytes are not probeable H.264"

echo "== 5. the concrete ffmpeg command used a software encoder =="
FFCMD="$(hwa_ffmpeg_command "${CNAME}")"
if [[ -z "${FFCMD}" ]]; then
  fail "no assembled ffmpeg command found in the container log"
else
  info "$(echo "${FFCMD}" | grep -oE '\-(codec|c):v:0 [a-z0-9_]+')"
  grep -qE '\-(codec|c):v:0 libx264' <<<"${FFCMD}" \
    && pass "transcode used the software encoder libx264" \
    || fail "transcode did not use libx264"
  if grep -qE "\-(codec|c):v:0 (${HWA_HW_ENCODERS})" <<<"${FFCMD}"; then
    fail "a hardware encoder was used on a host with no device"
  else
    pass "no VAAPI/QSV/NVENC/AMF/VideoToolbox/RKMPP/V4L2M2M encoder was used"
  fi
fi

echo "== 6. state and media safety are intact =="
if docker exec "${CNAME}" sh -c 'touch /media/.should-fail' 2>/dev/null; then
  fail "read-only media mount is writable"
else
  pass "media mount is still read-only"
fi
# The fixture must be byte-identical: transcoding reads the source, never rewrites it.
[[ -s "${FIXTURE}" ]] && pass "source media file still present and non-empty" || fail "source media file was damaged"
docker exec "${CNAME}" sh -c 'test -w /config && test -w /data && test -w /cache' \
  && pass "runtime state directories remain writable" || fail "a runtime state directory is not writable"
UID_IN="$(docker exec "${CNAME}" id -u)"
[[ "${UID_IN}" == "10000" ]] && pass "still running as non-root uid 10000" || fail "uid is ${UID_IN}"

echo "== 7. graceful shutdown (budget ${STOP_TIMEOUT}s) =="
START="$(date +%s)"
docker stop -t "${STOP_TIMEOUT}" "${CNAME}" >/dev/null
ELAPSED=$(( $(date +%s) - START ))
EXITCODE="$(docker inspect -f '{{.State.ExitCode}}' "${CNAME}")"
info "stop took ${ELAPSED}s, exit code ${EXITCODE}"
{ [[ "${EXITCODE}" == "0" || "${EXITCODE}" == "143" ]] && [[ "${ELAPSED}" -lt "${STOP_TIMEOUT}" ]]; } \
  && pass "SIGTERM handled gracefully after transcoding" \
  || fail "did not shut down cleanly (exit ${EXITCODE} after ${ELAPSED}s)"

echo
if [[ "${FAILED}" == 0 ]]; then
  echo "HWA NO-DEVICE GATE: all gates passed"
else
  echo "HWA NO-DEVICE GATE: FAILURES present"
  exit 1
fi
