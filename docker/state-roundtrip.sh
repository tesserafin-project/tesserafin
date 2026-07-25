#!/usr/bin/env bash
# shellcheck disable=SC2015,SC2317
# SC2015: `<cond> && pass || fail` is intended — pass()/fail() both return 0, so the
#         `|| fail` branch never fires on a true condition.
# SC2317: cleanup() is invoked indirectly via the EXIT trap.
# Persistent-state acceptance test for the Tesserafin container (#88 / [A2]).
#
# Proves the two measurable #88 gates end to end, with NO source checkout, and
# additionally asserts the backup/restore safety contract (confidentiality,
# forced-restore hidden-entry cleanup, restart-failure handling, archive-structure
# validation, JSON manifest validity, and bind-mount support incl. spaces):
#
#   Gate 1  A fresh container on empty volumes creates the schema via EF
#           migrations on first boot (asserted from the server log + the DB file).
#   Gate 2  A scripted backup.sh + restore.sh round-trip on a populated instance
#           restores the admin user, at least one library, and playback state,
#           verified by an automated before/after comparison.
#
# Safety assertions (each backs a claim made in the A2 docs / PR):
#   A  archive + sidecars are owned by the invoking user and are mode 0600.
#   B  restore --force removes pre-existing hidden (dot) entries before extract.
#   C  a failed post-backup restart exits non-zero and leaves the server stopped.
#   D  restore rejects archives with absolute / ".." / rogue top-level entries.
#   E  the default helper image is an immutable pinned digest, not a floating tag.
#   F  backup+restore work against host bind mounts, including paths with spaces.
#   G  the emitted manifest parses as valid JSON.
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
# Default image: the dev tag for THIS working tree (version source of truth +
# short commit), matching docker/build-clean.sh. No stale/hardcoded commit.
VERSION="$(grep -oP 'AssemblyVersion\("\K[0-9]+\.[0-9]+\.[0-9]+' "${REPO_ROOT}/SharedVersion.cs" | head -1)"
SHORT="$(git -C "${REPO_ROOT}" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"
IMAGE="${1:-ghcr.io/tesserafin-project/tesserafin:${VERSION}-dev.${SHORT}}"
PORT_A="${2:-19096}"
PORT_B="$((PORT_A + 1))"
# Immutable multi-arch busybox (matches backup.sh/restore.sh DEFAULT_HELPER).
HELPER="busybox:stable@sha256:73aaf090f3d85aa34ee199857f03fa3a95c8ede2ffd4cc2cdb5b94e566b11662"

ADMIN_USER="a2admin"
ADMIN_PASS="a2-round-trip-pass"
POS_TICKS=100000000             # 10s in ticks; ~33% of the 30s clip (below "played")
AUTH_HDR='MediaBrowser Client="tf-a2", Device="roundtrip", DeviceId="tf-a2-rt", Version="1.0"'

SFX="$$"
V_CFG="tf_a2_config_${SFX}"      ; V_CACHE="tf_a2_cache_${SFX}"      ; V_DATA="tf_a2_data_${SFX}"
V_CFG2="tf_a2_config2_${SFX}"    ; V_CACHE2="tf_a2_cache2_${SFX}"    ; V_DATA2="tf_a2_data2_${SFX}"
C_A="tf-a2-src-${SFX}"           ; C_B="tf-a2-restored-${SFX}"       ; C_RF="tf-a2-restartfail-${SFX}"
WORK="$(mktemp -d)"
MEDIA="${WORK}/media"
ARCHIVE="${WORK}/tesserafin-state-${SFX}.tgz"

FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }

cleanup() {
  docker rm -f "${C_A}" "${C_B}" "${C_RF}" >/dev/null 2>&1 || true
  docker volume rm "${V_CFG}" "${V_CACHE}" "${V_DATA}" \
                   "${V_CFG2}" "${V_CACHE2}" "${V_DATA2}" \
                   "tf_a2_rf_cfg_${SFX}" "tf_a2_rf_data_${SFX}" >/dev/null 2>&1 || true
  # The bind-mount test restores files owned by 10000:10000, which the invoking
  # host user cannot delete. Chown the whole work dir back via a root helper first
  # so `rm -rf` (and thus the script's own exit code) is not poisoned by leftovers.
  docker run --rm -v "${WORK}:/w" "${HELPER}" chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}" 2>/dev/null || true
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
# Seed a volume with a hidden + a visible junk entry (used for the --force test).
seed_junk() { # $1=cfg vol $2=data vol
  docker run --rm -v "${1}:/v/config" -v "${2}:/v/data" "${HELPER}" sh -c '
    echo stale > /v/config/.hidden_stale
    echo stale > /v/config/visible_stale.txt
    mkdir -p /v/data/.hidden_dir && echo stale > /v/data/.hidden_dir/x'
}

echo "== image under test: ${IMAGE} =="

# ===========================================================================
echo "== 0. prepare volumes + one media fixture =="
mkdir -p "${MEDIA}/Test Movie (2020)"
mkvol "${V_CFG}" "${V_CACHE}" "${V_DATA}"
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
# Herestrings, NOT `echo "${LOGA}" | grep -q`. Under `set -o pipefail` that pipeline
# reports failure even on a match: `grep -q` exits the instant it matches, `echo` is
# still writing, gets SIGPIPE (141), and pipefail propagates the 141. It only shows
# up once the log outgrows the pipe buffer, which JSON-lines logging (#91 / [A5])
# made it do — the assertions below were passing on luck, not on correctness.
grep -q "Initialise Migration service" <<<"${LOGA}" \
  && pass "migration service ran on first boot" \
  || fail "no migration-service log line on first boot"
grep -qi "Migration.*completed\|Seed migration\|Seed data" <<<"${LOGA}" \
  && pass "migrations/seed applied on first boot" \
  || fail "no migration/seed completion log on first boot"
DBFILES="$(docker run --rm -v "${V_DATA}:/data:ro" "${HELPER}" \
  sh -c 'find /data -maxdepth 3 -name "*.db" 2>/dev/null | head')"
[[ -n "${DBFILES}" ]] && pass "schema DB created on data volume ($(echo "${DBFILES}" | tr '\n' ' '))" \
  || fail "no *.db created under /data"

echo "== 2. populate via API: admin user, one library, playback state =="
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

ITEMID=""
for _ in $(seq 1 60); do
  ITEMS="$(apiget "${PORT_A}" "/Items?userId=${USERID}&recursive=true&includeItemTypes=Movie&fields=Path" "${TOKEN}" 2>/dev/null || true)"
  ITEMID="$(echo "${ITEMS}" | jq_py 'd["Items"][0]["Id"] if d.get("Items") else ""' 2>/dev/null || true)"
  [[ -n "${ITEMID}" ]] && break
  sleep 2
done
[[ -n "${ITEMID}" ]] && pass "library scan found the movie (item ${ITEMID})" || { fail "scan produced no item"; exit 1; }

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

echo "== 3. backup (server stopped for a consistent snapshot, then restarted) =="
"${REPO_ROOT}/docker/backup.sh" --out "${ARCHIVE}" \
  --config "${V_CFG}" --data "${V_DATA}" --container "${C_A}"
# The backup restarted C_A: prove the restart actually happened (hazard C, happy path).
[[ "$(docker inspect -f '{{.State.Running}}' "${C_A}" 2>/dev/null)" == "true" ]] \
  && pass "server restarted after backup (running)" || fail "server not running after backup"
docker rm -f "${C_A}" >/dev/null 2>&1 || true    # source instance is gone now

echo "== 3a. backup confidentiality: ownership + mode + manifest JSON (A, G) =="
ME="$(id -u):$(id -g)"
for f in "${ARCHIVE}" "${ARCHIVE}.sha256" "${ARCHIVE}.manifest.json"; do
  [[ -f "${f}" ]] || { fail "missing backup artifact ${f}"; continue; }
  own="$(stat -c '%u:%g' "${f}")"
  mode="$(stat -c '%a' "${f}")"
  [[ "${own}" == "${ME}" ]] && pass "$(basename "${f}") owned by invoking user (${own})" \
    || fail "$(basename "${f}") owned by ${own}, expected ${ME}"
  [[ "${mode}" == "600" ]] && pass "$(basename "${f}") mode 0600 (not group/world-readable)" \
    || fail "$(basename "${f}") mode ${mode}, expected 600"
done
python3 -c "import json,sys; json.load(open('${ARCHIVE}.manifest.json'))" \
  && pass "manifest is valid JSON (parser-verified)" || fail "manifest is not valid JSON"

echo "== 3b. archive-structure validation rejects hostile archives (D) =="
python3 - "${WORK}" <<'PY'
import tarfile, io, os, sys
work = sys.argv[1]
def mk(name, arc):
    with tarfile.open(os.path.join(work, name), "w:gz") as t:
        data = b"x"; ti = tarfile.TarInfo(arc); ti.size = len(data)
        t.addfile(ti, io.BytesIO(data))
mk("mal_abs.tgz",    "/etc/evil")
mk("mal_dotdot.tgz", "config/../../etc/evil")
mk("mal_rogue.tgz",  "etc/evil")
PY
for mal in mal_abs mal_dotdot mal_rogue; do
  if "${REPO_ROOT}/docker/restore.sh" --archive "${WORK}/${mal}.tgz" \
       --config "${V_CFG}" --data "${V_DATA}" >/dev/null 2>"${WORK}/${mal}.err"; then
    fail "restore accepted malformed archive ${mal}"
  else
    grep -q "REJECT" "${WORK}/${mal}.err" \
      && pass "restore rejected ${mal} ($(grep -o 'REJECT:[^\"]*' "${WORK}/${mal}.err" | head -1))" \
      || fail "restore failed on ${mal} but not via structure validation"
  fi
done

echo "== 3c. restart-failure handling exits non-zero, leaves server stopped (C) =="
mkvol "tf_a2_rf_cfg_${SFX}" "tf_a2_rf_data_${SFX}"
docker run -d --name "${C_RF}" "${HELPER}" sleep 600 >/dev/null
set +e
TF_RESTART_CMD=false "${REPO_ROOT}/docker/backup.sh" \
  --out "${WORK}/rf.tgz" --config "tf_a2_rf_cfg_${SFX}" --data "tf_a2_rf_data_${SFX}" \
  --container "${C_RF}" >"${WORK}/rf.out" 2>&1
RF_RC=$?
set -e
[[ "${RF_RC}" -ne 0 ]] && pass "restart-failure exits non-zero (rc=${RF_RC})" \
  || fail "restart-failure returned 0"
grep -q "remains STOPPED" "${WORK}/rf.out" && pass "restart-failure message states server stopped" \
  || fail "restart-failure message missing"
[[ "$(docker inspect -f '{{.State.Running}}' "${C_RF}" 2>/dev/null)" == "false" ]] \
  && pass "server left stopped after failed restart" || fail "server unexpectedly running"
docker rm -f "${C_RF}" >/dev/null 2>&1 || true

echo "== 4. restore into fresh volumes: seed junk, test guard + --force cleanup (B) =="
mkvol "${V_CFG2}" "${V_CACHE2}" "${V_DATA2}"
seed_junk "${V_CFG2}" "${V_DATA2}"
# 4a. non-empty guard: restore without --force must refuse.
if "${REPO_ROOT}/docker/restore.sh" --archive "${ARCHIVE}" \
     --config "${V_CFG2}" --data "${V_DATA2}" >/dev/null 2>&1; then
  fail "restore clobbered non-empty volumes without --force"
else
  pass "restore refused non-empty target without --force"
fi
# 4b. --force restores AND removes pre-existing hidden entries.
"${REPO_ROOT}/docker/restore.sh" --archive "${ARCHIVE}" \
  --config "${V_CFG2}" --data "${V_DATA2}" --force
chown_vols "${V_CACHE2}"
LEFTOVER="$(docker run --rm -v "${V_CFG2}:/v/config" -v "${V_DATA2}:/v/data" "${HELPER}" \
  sh -c 'find /v/config /v/data \( -name ".hidden_stale" -o -name "visible_stale.txt" -o -name ".hidden_dir" \) -print 2>/dev/null')"
[[ -z "${LEFTOVER}" ]] && pass "restore --force removed pre-existing hidden + visible junk" \
  || fail "stale entries survived --force: ${LEFTOVER}"

echo "== 5. boot restored instance + compare (Gate 2) =="
docker run -d --name "${C_B}" -p "127.0.0.1:${PORT_B}:8096" \
  -v "${V_CFG2}:/config" -v "${V_CACHE2}:/cache" -v "${V_DATA2}:/data" \
  -v "${MEDIA}:/media:ro" "${IMAGE}" >/dev/null
wait_api "${PORT_B}" || { echo "restored server never answered"; docker logs "${C_B}" | tail -40; exit 1; }
wait_ready "${PORT_B}" || { echo "restored server never left the 503 startup phase"; docker logs "${C_B}" | tail -40; exit 1; }

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
docker rm -f "${C_B}" >/dev/null 2>&1 || true

echo "== 6. bind-mount support incl. paths with spaces (F) =="
BM_CFG="${WORK}/bind config"; BM_DATA="${WORK}/bind data"
mkdir -p "${BM_CFG}" "${BM_DATA}"
"${REPO_ROOT}/docker/restore.sh" --archive "${ARCHIVE}" \
  --config "${BM_CFG}" --data "${BM_DATA}"
BM_DB="$(docker run --rm -v "${BM_DATA}:/data:ro" "${HELPER}" \
  sh -c 'find /data -maxdepth 3 -name "*.db" 2>/dev/null | head')"
[[ -n "${BM_DB}" ]] && pass "restore into bind mount with spaces populated the DB" \
  || fail "bind-mount restore produced no DB"
"${REPO_ROOT}/docker/backup.sh" --out "${WORK}/bind space backup.tgz" \
  --config "${BM_CFG}" --data "${BM_DATA}"
[[ -s "${WORK}/bind space backup.tgz" ]] \
  && pass "backup from bind mount with spaces produced an archive" \
  || fail "bind-mount backup produced no archive"

echo "== 7. helper image is an immutable pinned digest (E) =="
for s in backup.sh restore.sh; do
  if grep -q 'DEFAULT_HELPER="busybox:stable@sha256:' "${REPO_ROOT}/docker/${s}"; then
    pass "${s} default helper is a pinned digest"
  else
    fail "${s} default helper is not a pinned digest"
  fi
done

echo
if [[ "${FAILED}" == 0 ]]; then
  echo "ROUNDTRIP: all gates + safety assertions passed"; exit 0
else
  echo "ROUNDTRIP: FAILED"; exit 1
fi
