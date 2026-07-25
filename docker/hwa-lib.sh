#!/usr/bin/env bash
# Shared helpers for the A4 hardware-acceleration acceptance gates (#90).
#
# Sourced by docker/hwa-smoke.sh (no device) and docker/hwa-vaapi.sh (real GPU).
# Everything here drives a RUNNING PRODUCTION CONTAINER over HTTP — nothing stubs
# the server, and nothing asserts on unit-test output. The point of these gates is
# that real media bytes come back out of a real ffmpeg the image actually shipped.
#
# shellcheck shell=bash

# Immutable multi-arch busybox, matching docker/state-roundtrip.sh.
HWA_HELPER="busybox:stable@sha256:73aaf090f3d85aa34ee199857f03fa3a95c8ede2ffd4cc2cdb5b94e566b11662"

HWA_ADMIN_USER="a4admin"
HWA_ADMIN_PASS="a4-hwa-gate-pass"
HWA_AUTH_HDR='MediaBrowser Client="tf-a4", Device="hwa-gate", DeviceId="tf-a4-hwa", Version="1.0"'

FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }
info() { echo "  ....  $*"; }

# --- HTTP helpers ----------------------------------------------------------
HWA_RETRY=(--retry 6 --retry-delay 2 --retry-all-errors)

api_get() { # $1=port $2=path $3=token("" for none)
  local hdr="${HWA_AUTH_HDR}"; [[ -n "${3:-}" ]] && hdr="${HWA_AUTH_HDR}, Token=\"${3}\""
  curl -fsS "${HWA_RETRY[@]}" "http://127.0.0.1:${1}${2}" -H "Authorization: ${hdr}"
}

api_post() { # $1=port $2=path $3=token("" for none) [$4=json-body]
  local p="$1" path="$2" tok="${3:-}" body="${4:-}"
  local hdr="${HWA_AUTH_HDR}"; [[ -n "${tok}" ]] && hdr="${HWA_AUTH_HDR}, Token=\"${tok}\""
  if [[ -n "${body}" ]]; then
    curl -fsS "${HWA_RETRY[@]}" -X POST "http://127.0.0.1:${p}${path}" \
      -H "Authorization: ${hdr}" -H 'Content-Type: application/json' -d "${body}"
  else
    curl -fsS "${HWA_RETRY[@]}" -X POST "http://127.0.0.1:${p}${path}" -H "Authorization: ${hdr}"
  fi
}

json_field() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)"; }

# Stably past the startup phase. /System/Info/Public is answered by the startup
# SetupServer while the application is still coming up, so it is liveness only.
wait_ready() { # $1=port
  local p="$1" code streak=0
  for _ in $(seq 1 150); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${p}/Startup/Configuration" 2>/dev/null || echo 000)"
    if [[ "${code}" != "503" && "${code}" != "000" ]]; then
      streak=$((streak + 1))
      [[ "${streak}" -ge 5 ]] && return 0
    else
      streak=0
    fi
    sleep 2
  done
  return 1
}

# --- media fixture ---------------------------------------------------------
# MPEG-2 video + MP2 audio in Matroska. The codec choice is load-bearing: an
# H.264/AAC MP4 would be direct-played or remuxed and would yield media bytes
# with no encoder running at all, which would make this gate pass without ever
# proving a transcode. Nothing can serve this file as H.264 without re-encoding.
hwa_make_fixture() { # $1=image $2=host media dir
  mkdir -p "${2}/Sample Movie (2021)"
  docker run --rm --user 0:0 --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg \
    -v "${2}:/m" "${1}" \
    -y -f lavfi -i "testsrc=duration=20:size=640x480:rate=25" \
       -f lavfi -i "sine=frequency=440:duration=20" \
       -c:v mpeg2video -b:v 2M -pix_fmt yuv420p -c:a mp2 -shortest \
       "/m/Sample Movie (2021)/Sample Movie (2021).mkv" >/dev/null 2>&1
  docker run --rm --user 0:0 -v "${2}:/m" --entrypoint chown "${1}" -R "$(id -u):$(id -g)" /m
}

# --- onboarding ------------------------------------------------------------
# Completes the first-run wizard and returns "<token> <userId>". Enough setup to
# request playback, and no more: this gate is about transcoding, not onboarding
# (which A3 already covers through a real browser).
hwa_onboard() { # $1=port
  local p="$1" auth token userid
  api_get "${p}" "/Startup/User" "" >/dev/null
  api_post "${p}" "/Startup/User" "" \
    "{\"Name\":\"${HWA_ADMIN_USER}\",\"Password\":\"${HWA_ADMIN_PASS}\"}" >/dev/null
  api_post "${p}" "/Startup/Complete" "" >/dev/null
  auth="$(api_post "${p}" "/Users/AuthenticateByName" "" \
    "{\"Username\":\"${HWA_ADMIN_USER}\",\"Pw\":\"${HWA_ADMIN_PASS}\"}")"
  token="$(echo "${auth}" | json_field 'd["AccessToken"]')"
  userid="$(echo "${auth}" | json_field 'd["User"]["Id"]')"
  [[ -n "${token}" && -n "${userid}" ]] || return 1
  echo "${token} ${userid}"
}

# Adds /media as a movie library and waits for the scan to surface the fixture.
hwa_library_item() { # $1=port $2=token $3=userId
  local p="$1" tok="$2" uid="$3" items id
  api_post "${p}" "/Library/VirtualFolders?name=Movies&collectionType=movies&paths=%2Fmedia&refreshLibrary=true" \
    "${tok}" '{"LibraryOptions":{"PathInfos":[{"Path":"/media"}]}}' >/dev/null
  for _ in $(seq 1 60); do
    items="$(api_get "${p}" "/Items?userId=${uid}&recursive=true&includeItemTypes=Movie&fields=Path" "${tok}" 2>/dev/null || true)"
    id="$(echo "${items}" | json_field 'd["Items"][0]["Id"] if d.get("Items") else ""' 2>/dev/null || true)"
    [[ -n "${id}" ]] && { echo "${id}"; return 0; }
    sleep 2
  done
  return 1
}

# Forces a real transcode and writes the returned bytes to $5. Requesting H.264
# from an MPEG-2 source with static=false leaves the server no direct-play or
# remux option.
hwa_transcode() { # $1=port $2=token $3=itemId $4=outfile -> prints "http=<code> bytes=<n>"
  curl -s -o "${4}" -w 'http=%{http_code} bytes=%{size_download}' --max-time 180 \
    "http://127.0.0.1:${1}/Videos/${3}/stream.mp4?static=false&videoCodec=h264&audioCodec=aac&container=mp4&mediaSourceId=${3}&api_key=${2}"
}

# The concrete ffmpeg command the server assembled, straight from its own log.
# This is the authority for which encoder actually ran — not the requested codec,
# not the configuration, and not any unit test's opinion of what would be chosen.
hwa_ffmpeg_command() { # $1=container
  docker logs "${1}" 2>&1 | grep -F 'jellyfin-ffmpeg/ffmpeg' | grep -E '\-codec:v:0|\-c:v:0' | tail -1
}

# The one conclusive startup decision event.
hwa_decision_line() { # $1=container
  docker logs "${1}" 2>&1 | grep -F 'Hardware acceleration decision:' | tail -1
}

# Asserts the structured decision fields. Every field is matched as a whole word
# so Backend=none cannot satisfy a check for Backend=nvenc and so on.
hwa_assert_decision() { # $1=container $2=expected Mode $3=expected Backend $4=regex of acceptable Reason
  local line; line="$(hwa_decision_line "${1}")"
  if [[ -z "${line}" ]]; then
    fail "no conclusive hardware acceleration decision was logged"
    return 1
  fi
  info "decision: ${line#*Hardware acceleration decision: }"
  grep -qE "Mode=${2}( |$)" <<<"${line}" && pass "decision Mode=${2}" || fail "decision Mode is not ${2}"
  grep -qE "Backend=${3}( |$)" <<<"${line}" && pass "decision Backend=${3}" || fail "decision Backend is not ${3}"
  grep -qE "Reason=(${4})( |$)" <<<"${line}" && pass "decision Reason matches ${4}" || fail "decision Reason is not one of ${4}"
}

# The seven hardware encoders that must never appear in a software transcode.
HWA_HW_ENCODERS='h264_vaapi|hevc_vaapi|av1_vaapi|h264_qsv|hevc_qsv|av1_qsv|h264_nvenc|hevc_nvenc|av1_nvenc|h264_amf|hevc_amf|av1_amf|h264_videotoolbox|hevc_videotoolbox|h264_rkmpp|hevc_rkmpp|h264_v4l2m2m'
