#!/usr/bin/env bash
# Persistent-state acceptance test for the Tesserafin container (#88 / [A2]).
#
# Proves the two measurable #88 gates end to end, with NO source checkout:
#   Gate 1  A fresh container on empty volumes creates the schema via EF
#           migrations on first boot (asserted from the server log + the DB file).
#   Gate 2  A scripted backup.sh + restore.sh round-trip on a populated instance
#           restores the admin user, at least one library, and playback state,
#           verified by an automated before/after comparison.
#
# The instance is populated through the public API only (startup wizard, library
# creation + scan of one generated media file, playback-progress report). Backup
# and restore are driven by docker/backup.sh and docker/restore.sh against docker
# NAMED VOLUMES; the restore lands in a second, independent set of volumes so the
# round-trip proves portability, not in-place mutation.
#
# Usage: docker/state-roundtrip.sh [<image-ref>] [<host-port>]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="${1:-ghcr.io/tesserafin-project/tesserafin:12.0.0-dev.0750409a3e02}"
PORT_A="${2:-19096}"
PORT_B="$((PORT_A + 1))"
HELPER="busybox:stable"

ADMIN_USER="a2admin"
ADMIN_PASS="a2-round-trip-pass"
POS_TICKS=100000000             # 10s in ticks; ~33% of the 30s clip (below "played")
AUTH_HDR='MediaBrowser Client="tf-a2", Device="roundtrip", DeviceId="tf-a2-rt", Version="1.0"'

SFX="$$"
V_CFG="tf_a2_config_${SFX}"      ; V_CACHE="tf_a2_cache_${SFX}"      ; V_DATA="tf_a2_data_${SFX}"
V_CFG2="tf_a2_config2_${SFX}"    ; V_CACHE2="tf_a2_cache2_${SFX}"    ; V_DATA2="tf_a2_data2_${SFX}"
C_A="tf-a2-src-${SFX}"           ; C_B="tf-a2-restored-${SFX}"
WORK="$(mktemp -d)"
MEDIA="${WORK}/media"
ARCHIVE="${WORK}/tesserafin-state-${SFX}.tgz"

FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }

cleanup() {
  docker rm -f "${C_A}" "${C_B}" >/dev/null 2>&1 || true
  docker volume rm "${V_CFG}" "${V_CACHE}" "${V_DATA}" \
                   "${V_CFG2}" "${V_CACHE2}" "${V_DATA2}" >/dev/null 2>&1 || true
  rm -rf "${WORK}"
}
trap cleanup EXIT

# --- helpers ---------------------------------------------------------------
mkvol()  { for v in "$@"; do docker volume create "$v" >/dev/null; done; }
chown_vols() { # $@ = volume names -> owned by 10000:10000
  local args=(); for v in "$@"; do args+=(-v "${v}:/v/${v}"); done
  docker run --rm "${args[@]}" "${HELPER}" sh -c 'chown -R 10000:10000 /v/*'
}
wait_api() { # $1=port -> liveness: the public info endpoint answers
  local p="$1"
  for _ in $(seq 1 90); do
    curl -fsS "http://127.0.0.1:${p}/System/Info/Public" >/dev/null 2>&1 && return 0
    sleep 2
  done
  return 1
}
wait_ready() { # $1=port -> STABLY past the startup phase (rides the first-boot restart)
  # The server can report ready, then restart once early in first-boot init. Require
  # several consecutive non-503 responses so we don't proceed inside that window.
  # Accepts any non-503/non-000 code: 200 before the wizard, 403 after it completes.
  local p="$1" code streak=0
  for _ in $(seq 1 150); do   # up to ~300s
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
# Kestrel can reset the odd connection just after the server reports ready (init/GC
# spike). --retry-all-errors + --retry rides over those transient 56/5xx blips; the
# wizard/auth/progress calls used here are idempotent so a retry is safe.
RETRY=(--retry 6 --retry-delay 2 --retry-all-errors)
apipost() { # $1=port $2=path $3=token("" for none) [$4=json-body]
  local p="$1" path="$2" tok="$3" body="${4:-}"
  local hdr="${AUTH_HDR}"; [[ -n "${tok}" ]] && hdr="${AUTH_HDR}, Token=\"${tok}\""
  if [[ -n "${body}" ]]; then
    curl -fsS "${RETRY[@]}" -X POST "http://127.0.0.1:${p}${path}" \
      -H "Authorization: ${hdr}" -H 'Content-Type: application/json' -d "${body}"
  else
    curl -fsS "${RETRY[@]}" -X POST "http://127.0.0.1:${p}${path}" -H "Authorization: ${hdr}"
  fi
}
apiget() { # $1=port $2=path $3=token("" for none)
  local hdr="${AUTH_HDR}"; [[ -n "${3}" ]] && hdr="${AUTH_HDR}, Token=\"${3}\""
  curl -fsS "${RETRY[@]}" "http://127.0.0.1:${1}${2}" -H "Authorization: ${hdr}"
}
jq_py() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)"; }
playstate() { # $1=port $2=userId $3=itemId $4=token -> "Played=<bool>|PlayCount=<n>"
  apiget "$1" "/Items?userId=$2&ids=$3" "$4" \
    | jq_py '"Played={}|PlayCount={}".format(d["Items"][0]["UserData"]["Played"], d["Items"][0]["UserData"]["PlayCount"]) if d.get("Items") else "MISSING"'
}

# ===========================================================================
echo "== 0. prepare volumes + one media fixture =="
mkdir -p "${MEDIA}/Test Movie (2020)"
mkvol "${V_CFG}" "${V_CACHE}" "${V_DATA}"
# Generate a tiny, real, playable mp4 with the image's own bundled ffmpeg.
# Run the helper as root: the host-owned bind mount is not writable by the image's
# non-root uid (10000). The resulting file is world-readable for the read-only scan.
docker run --rm --user 0:0 --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg \
  -v "${MEDIA}:/m" "${IMAGE}" \
  -y -f lavfi -i testsrc=duration=30:size=160x120:rate=5 \
  -pix_fmt yuv420p "/m/Test Movie (2020)/Test Movie (2020).mp4" >/dev/null 2>&1
chown_vols "${V_CFG}" "${V_CACHE}" "${V_DATA}"

echo "== 1. first boot on empty volumes (Gate 1: migrations create the schema) =="
docker run -d --name "${C_A}" -p "127.0.0.1:${PORT_A}:8096" \
  -v "${V_CFG}:/config" -v "${V_CACHE}:/cache" -v "${V_DATA}:/data" \
  -v "${MEDIA}:/media:ro" "${IMAGE}" >/dev/null
wait_api "${PORT_A}" || { echo "server A never answered"; docker logs "${C_A}" | tail -40; exit 1; }
wait_ready "${PORT_A}" || { echo "server A never left the 503 startup phase"; docker logs "${C_A}" | tail -40; exit 1; }

LOGA="$(docker logs "${C_A}" 2>&1)"
echo "${LOGA}" | grep -q "Initialise Migration service" \
  && pass "migration service ran on first boot" \
  || fail "no migration-service log line on first boot"
echo "${LOGA}" | grep -qi "Migration.*completed\|Seed migration\|Seed data" \
  && pass "migrations/seed applied on first boot" \
  || fail "no migration/seed completion log on first boot"
DBFILES="$(docker run --rm -v "${V_DATA}:/data:ro" "${HELPER}" \
  sh -c 'find /data -maxdepth 3 -name "*.db" 2>/dev/null | head')"
[[ -n "${DBFILES}" ]] && pass "schema DB created on data volume ($(echo "${DBFILES}" | tr '\n' ' '))" \
  || fail "no *.db created under /data"

echo "== 2. populate via API: admin user, one library, playback state =="
# Minimal wizard: GET /Startup/User initialises the default user; POST /Startup/User
# then renames it and sets its password (POST returns 404 without the prior GET).
# Configuration/RemoteAccess are left at their defaults (already en-US/US/en).
apiget "${PORT_A}" "/Startup/User" "" >/dev/null
apipost "${PORT_A}" "/Startup/User" "" \
  "{\"Name\":\"${ADMIN_USER}\",\"Password\":\"${ADMIN_PASS}\"}" >/dev/null
apipost "${PORT_A}" "/Startup/Complete" "" >/dev/null

AUTH_JSON="$(apipost "${PORT_A}" "/Users/AuthenticateByName" "" \
  "{\"Username\":\"${ADMIN_USER}\",\"Pw\":\"${ADMIN_PASS}\"}")"
TOKEN="$(echo "${AUTH_JSON}" | jq_py 'd["AccessToken"]')"
USERID="$(echo "${AUTH_JSON}" | jq_py 'd["User"]["Id"]')"
[[ -n "${TOKEN}" && -n "${USERID}" ]] && pass "authenticated as ${ADMIN_USER}" || { fail "auth failed"; exit 1; }

apipost "${PORT_A}" "/Library/VirtualFolders?name=Movies&collectionType=movies&paths=%2Fmedia&refreshLibrary=true" \
  "${TOKEN}" '{"LibraryOptions":{"PathInfos":[{"Path":"/media"}]}}' >/dev/null

# Wait for the scan to surface the movie.
ITEMID=""
for _ in $(seq 1 60); do
  ITEMS="$(apiget "${PORT_A}" "/Items?userId=${USERID}&recursive=true&includeItemTypes=Movie&fields=Path" "${TOKEN}" 2>/dev/null || true)"
  ITEMID="$(echo "${ITEMS}" | jq_py 'd["Items"][0]["Id"] if d.get("Items") else ""' 2>/dev/null || true)"
  [[ -n "${ITEMID}" ]] && break
  sleep 2
done
[[ -n "${ITEMID}" ]] && pass "library scan found the movie (item ${ITEMID})" || { fail "scan produced no item"; exit 1; }

# Report a full playback session (start -> progress -> stopped). This persists the
# watched-state in UserData: Played=true, PlayCount=1, LastPlayedDate. (A resume
# PlaybackPositionTicks is only kept when the item's RunTimeTicks is known; the
# synthetic fixture has none, so the server records it as fully played instead —
# watched-state is the stable, verifiable playback datum here.)
apipost "${PORT_A}" "/Sessions/Playing" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PlayMethod\":\"DirectPlay\",\"CanSeek\":true}" >/dev/null
apipost "${PORT_A}" "/Sessions/Playing/Progress" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PlayMethod\":\"DirectPlay\",\"PositionTicks\":${POS_TICKS},\"IsPaused\":false}" >/dev/null
apipost "${PORT_A}" "/Sessions/Playing/Stopped" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PositionTicks\":${POS_TICKS}}" >/dev/null
BEFORE_PLAY="$(playstate "${PORT_A}" "${USERID}" "${ITEMID}" "${TOKEN}")"
BEFORE_USERS="$(apiget "${PORT_A}" "/Users" "${TOKEN}" | jq_py 'sorted(u["Name"] for u in d)')"
BEFORE_LIBS="$(apiget "${PORT_A}" "/Library/VirtualFolders" "${TOKEN}" | jq_py 'sorted(f["Name"] for f in d)')"
echo "  before: users=${BEFORE_USERS} libs=${BEFORE_LIBS} play=${BEFORE_PLAY}"
[[ "${BEFORE_PLAY}" == "Played=True|PlayCount=1" ]] && pass "playback state recorded (${BEFORE_PLAY})" \
  || fail "playback state not stored (got ${BEFORE_PLAY})"

echo "== 3. backup (server stopped for a consistent snapshot) =="
"${REPO_ROOT}/docker/backup.sh" --out "${ARCHIVE}" \
  --config "${V_CFG}" --data "${V_DATA}" --container "${C_A}"
docker rm -f "${C_A}" >/dev/null 2>&1 || true    # source instance is gone now

echo "== 4. restore into fresh, independent volumes =="
mkvol "${V_CFG2}" "${V_CACHE2}" "${V_DATA2}"
"${REPO_ROOT}/docker/restore.sh" --archive "${ARCHIVE}" \
  --config "${V_CFG2}" --data "${V_DATA2}"
chown_vols "${V_CACHE2}"

echo "== 5. boot restored instance + compare (Gate 2) =="
docker run -d --name "${C_B}" -p "127.0.0.1:${PORT_B}:8096" \
  -v "${V_CFG2}:/config" -v "${V_CACHE2}:/cache" -v "${V_DATA2}:/data" \
  -v "${MEDIA}:/media:ro" "${IMAGE}" >/dev/null
wait_api "${PORT_B}" || { echo "restored server never answered"; docker logs "${C_B}" | tail -40; exit 1; }
wait_ready "${PORT_B}" || { echo "restored server never left the 503 startup phase"; docker logs "${C_B}" | tail -40; exit 1; }

# Same admin credentials must work -> the user DB was restored.
AUTH2="$(apipost "${PORT_B}" "/Users/AuthenticateByName" "" \
  "{\"Username\":\"${ADMIN_USER}\",\"Pw\":\"${ADMIN_PASS}\"}")"
TOKEN2="$(echo "${AUTH2}" | jq_py 'd["AccessToken"]')"
USERID2="$(echo "${AUTH2}" | jq_py 'd["User"]["Id"]')"
[[ -n "${TOKEN2}" ]] && pass "restored admin credentials authenticate" || { fail "restored auth failed"; exit 1; }

AFTER_USERS="$(apiget "${PORT_B}" "/Users" "${TOKEN2}" | jq_py 'sorted(u["Name"] for u in d)')"
AFTER_LIBS="$(apiget "${PORT_B}" "/Library/VirtualFolders" "${TOKEN2}" | jq_py 'sorted(f["Name"] for f in d)')"
AFTER_PLAY="$(playstate "${PORT_B}" "${USERID2}" "${ITEMID}" "${TOKEN2}")"
echo "  after:  users=${AFTER_USERS} libs=${AFTER_LIBS} play=${AFTER_PLAY}"

[[ "${AFTER_USERS}" == "${BEFORE_USERS}" ]] && pass "users restored identically" || fail "user set differs"
[[ "${AFTER_LIBS}"  == "${BEFORE_LIBS}"  ]] && pass "libraries restored identically" || fail "library set differs"
[[ "${AFTER_PLAY}"  == "${BEFORE_PLAY}" && "${AFTER_PLAY}" != "MISSING" ]] \
  && pass "playback state restored (${AFTER_PLAY})" || fail "playback state differs (${AFTER_PLAY} != ${BEFORE_PLAY})"

echo
if [[ "${FAILED}" == 0 ]]; then
  echo "ROUNDTRIP: all gates passed"; exit 0
else
  echo "ROUNDTRIP: FAILED"; exit 1
fi
