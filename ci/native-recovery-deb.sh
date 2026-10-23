#!/usr/bin/env bash
# shellcheck disable=SC2015,SC2317,SC2329
# SC2015: `<cond> && pass || fail` is intended — pass()/fail() both return 0.
# SC2317/SC2329: cleanup() is invoked indirectly via the EXIT trap.
#
# Native Linux (.deb) cold-snapshot recovery rehearsal (#144).
#
# Installs one published, digest-pinned tesserafin-server .deb, populates it
# through the public API (same fixture and oracle as docker/state-roundtrip.sh),
# stops the service, archives /etc/tesserafin and /var/lib/tesserafin, then
# destroys the installation: package purged, every persistent directory and the
# service account removed. It reinstalls the same package, restores the archive
# and compares state with the original pre-backup tokens.
#
# DESTRUCTIVE. Run only as root on a disposable host that never held Tesserafin
# (a hosted runner). It refuses unless TF_NATIVE_RECOVERY_DISPOSABLE=1 and the host
# has no package, account or persistent directory of its own. Same host, same
# package: no forward migration, no fresh-disk/off-host transfer.
#
# Usage: TF_NATIVE_RECOVERY_DISPOSABLE=1 ci/native-recovery-deb.sh <deb> <sha256>
set -euo pipefail
umask 077

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEB="$(realpath "${1:?usage: native-recovery-deb.sh <deb> <sha256>}")"
DEB_SHA256="${2:?usage: native-recovery-deb.sh <deb> <sha256>}"
PORT=8096
ADMIN_USER="a2admin"
ADMIN_PASS="a2-round-trip-pass"
POS_TICKS=100000000
AUTH_HDR='MediaBrowser Client="tf-a2", Device="roundtrip", DeviceId="tf-a2-rt", Version="1.0"'
STATE_DIRS=(etc/tesserafin var/lib/tesserafin)
ALL_DIRS=(/etc/tesserafin /var/lib/tesserafin /var/cache/tesserafin /var/log/tesserafin)
MEDIA=/media
MOVIE="${MEDIA}/Test Movie (2020)"
WORK="$(mktemp -d)"
ARCHIVE="${WORK}/tesserafin-native-state.tgz"

FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }

[[ "${TF_NATIVE_RECOVERY_DISPOSABLE:-}" == 1 && "${EUID}" == 0 ]] \
  || { echo "refusing: run as root on a disposable host with TF_NATIVE_RECOVERY_DISPOSABLE=1" >&2; exit 2; }
[[ "${DEB_SHA256}" =~ ^[0-9a-f]{64}$ ]] || { echo "expected a SHA-256 hex digest" >&2; exit 2; }

cleanup() { rm -rf "${WORK}"; }
trap cleanup EXIT

# --- helpers (same API calls and readiness rule as docker/state-roundtrip.sh) ---
wait_ready() { # STABLY past core startup: only /health 200 means ready
  local code streak=0
  for _ in $(seq 1 150); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${PORT}/health" 2>/dev/null || echo 000)"
    if [[ "${code}" == "200" ]]; then
      streak=$((streak + 1))
      [[ "${streak}" -ge 5 ]] && return 0
    else
      streak=0
    fi
    sleep 2
  done
  return 1
}
http_code() { # $1=path [$2=token]
  local hdr="${AUTH_HDR}"; [[ -n "${2:-}" ]] && hdr="${AUTH_HDR}, Token=\"${2}\""
  curl -s -o /dev/null -w '%{http_code}' --max-time 10 "http://127.0.0.1:${PORT}${1}" \
    -H "Authorization: ${hdr}" 2>/dev/null || echo 000
}
# Allowlisted diagnostics (ci/recovery-evidence.py): systemd counters/state words and
# HTTP status codes only. Never bodies, tokens or journal text.
diag() { # $1=label [$2=token]
  local restarts state exit_status
  restarts="$(systemctl show -P NRestarts tesserafin 2>/dev/null || echo 0)"
  state="$(systemctl show -P ActiveState tesserafin 2>/dev/null || echo missing)"
  exit_status="$(systemctl show -P ExecMainStatus tesserafin 2>/dev/null || echo 0)"
  local line="DIAG $1 restart_count=${restarts:-0} status=${state:-missing} exit_code=${exit_status:-0}"
  line+=" health_http=$(http_code /health) startup_config_http=$(http_code /Startup/Configuration)"
  [[ -n "${2:-}" ]] && line+=" users_me_http=$(http_code /Users/Me "$2")"
  echo "${line}"
}
RETRY=(--retry 6 --retry-delay 2 --retry-all-errors)
apipost() { # $1=path $2=token("" for none) [$3=json-body]
  local hdr="${AUTH_HDR}"; [[ -n "$2" ]] && hdr="${AUTH_HDR}, Token=\"$2\""
  if [[ -n "${3:-}" ]]; then
    curl -fsS "${RETRY[@]}" -X POST "http://127.0.0.1:${PORT}$1" \
      -H "Authorization: ${hdr}" -H 'Content-Type: application/json' -d "$3"
  else
    curl -fsS "${RETRY[@]}" -X POST "http://127.0.0.1:${PORT}$1" -H "Authorization: ${hdr}"
  fi
}
apiget() { # $1=path $2=token("" for none)
  local hdr="${AUTH_HDR}"; [[ -n "$2" ]] && hdr="${AUTH_HDR}, Token=\"$2\""
  curl -fsS "${RETRY[@]}" "http://127.0.0.1:${PORT}$1" -H "Authorization: ${hdr}"
}
jq_py() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)"; }
playstate() { # $1=userId $2=itemId $3=token
  apiget "/Items?userId=$1&ids=$2" "$3" \
    | jq_py '"Played={}|PlayCount={}".format(d["Items"][0]["UserData"]["Played"], d["Items"][0]["UserData"]["PlayCount"]) if d.get("Items") else "MISSING"'
}
install_deb() {
  [[ "$(sha256sum "${DEB}" | cut -d' ' -f1)" == "${DEB_SHA256}" ]] \
    && pass "package matches pinned SHA-256 before install" \
    || { fail "package does not match pinned SHA-256"; exit 1; }
  DEBIAN_FRONTEND=noninteractive apt-get install -y -q "${DEB}" >/dev/null
  # A first install enables the unit but leaves it stopped (docs/install.md).
  [[ "$(systemctl is-enabled tesserafin)" == enabled ]] && ! systemctl is-active --quiet tesserafin \
    && pass "fresh install: service enabled and stopped" \
    || fail "fresh install did not leave the service enabled and stopped"
}
# Owner, group, mode and content of every persistent entry, for the byte-level
# before/after comparison. Kept private in WORK; never printed.
state_listing() {
  ( cd / && find "${STATE_DIRS[@]}" -printf '%p %u:%g %m %y\n' | sort
    find "${STATE_DIRS[@]}" -type f -print0 | sort -z | xargs -0r sha256sum )
}
service_stopped() {
  ! systemctl is-active --quiet tesserafin && ! pgrep -u tesserafin >/dev/null 2>&1
}

echo "== 0. prepare: disposable host, pinned package, media fixture =="
CLEAN=1
dpkg -s tesserafin-server >/dev/null 2>&1 && CLEAN=0
getent passwd tesserafin >/dev/null && CLEAN=0
for d in "${ALL_DIRS[@]}"; do [[ -e "${d}" ]] && CLEAN=0; done
[[ -e "${MEDIA}" && -n "$(ls -A "${MEDIA}")" ]] && CLEAN=0
[[ "${CLEAN}" == 1 ]] && pass "host holds no Tesserafin package, account, state or media" \
  || { fail "host is not a clean disposable host"; exit 1; }

echo "== 1. install the published package and first boot =="
install_deb
mkdir -p "${MOVIE}"
chmod 755 "${MEDIA}" "${MOVIE}"
/usr/lib/tesserafin/ffmpeg/bin/ffmpeg -nostdin -y -f lavfi -i testsrc=duration=30:size=160x120:rate=5 \
  -pix_fmt yuv420p "${MOVIE}/Test Movie (2020).mp4" >/dev/null 2>&1
chmod 644 "${MOVIE}/Test Movie (2020).mp4"
runuser -u tesserafin -- test -r "${MOVIE}/Test Movie (2020).mp4" \
  && pass "generated fixture readable as the service account" \
  || { fail "generated fixture unreadable as the service account"; exit 1; }
systemctl start tesserafin
wait_ready || { diag first-boot-never-ready; echo "server never left the 503 startup phase"; exit 1; }
diag first-boot-ready
[[ -n "$(find /var/lib/tesserafin -maxdepth 3 -name '*.db' | head -1)" ]] \
  && pass "schema DB created under /var/lib/tesserafin" || fail "no *.db under /var/lib/tesserafin"

echo "== 2. populate via API: admin user, one library, playback state =="
apiget "/Startup/User" "" >/dev/null
apipost "/Startup/User" "" "{\"Name\":\"${ADMIN_USER}\",\"Password\":\"${ADMIN_PASS}\"}" >/dev/null
apipost "/Startup/Complete" "" >/dev/null
AUTH_JSON="$(apipost "/Users/AuthenticateByName" "" "{\"Username\":\"${ADMIN_USER}\",\"Pw\":\"${ADMIN_PASS}\"}")"
TOKEN="$(echo "${AUTH_JSON}" | jq_py 'd["AccessToken"]')"
USERID="$(echo "${AUTH_JSON}" | jq_py 'd["User"]["Id"]')"
[[ -n "${TOKEN}" && -n "${USERID}" ]] && pass "authenticated as ${ADMIN_USER}" || { fail "auth failed"; exit 1; }
apipost "/Library/VirtualFolders?name=Movies&collectionType=movies&paths=%2Fmedia&refreshLibrary=true" \
  "${TOKEN}" '{"LibraryOptions":{"PathInfos":[{"Path":"/media"}]}}' >/dev/null
ITEMID=""
for _ in $(seq 1 60); do
  ITEMID="$(apiget "/Items?userId=${USERID}&recursive=true&includeItemTypes=Movie" "${TOKEN}" 2>/dev/null \
    | jq_py 'd["Items"][0]["Id"] if d.get("Items") else ""' 2>/dev/null || true)"
  [[ -n "${ITEMID}" ]] && break
  sleep 2
done
[[ -n "${ITEMID}" ]] && pass "library scan found the movie (item ${ITEMID})" || { fail "scan produced no item"; exit 1; }
apipost "/Sessions/Playing" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PlayMethod\":\"DirectPlay\",\"CanSeek\":true}" >/dev/null
apipost "/Sessions/Playing/Progress" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PlayMethod\":\"DirectPlay\",\"PositionTicks\":${POS_TICKS},\"IsPaused\":false}" >/dev/null
apipost "/Sessions/Playing/Stopped" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PositionTicks\":${POS_TICKS}}" >/dev/null
BEFORE_PLAY="$(playstate "${USERID}" "${ITEMID}" "${TOKEN}")"
BEFORE_LIBS="$(apiget "/Library/VirtualFolders" "${TOKEN}" | jq_py 'sorted(f["Name"] for f in d)')"
[[ "${BEFORE_PLAY}" == "Played=True|PlayCount=1" ]] && pass "playback state recorded (${BEFORE_PLAY})" \
  || fail "playback state not stored (got ${BEFORE_PLAY})"

echo "== 2a. seed and capture extended per-user recovery state =="
TF_RECOVERY_TOKEN="${TOKEN}" python3 "${REPO_ROOT}/docker/recovery-state.py" \
  seed "${PORT}" "${USERID}" "${ITEMID}" > "${WORK}/recovery-fixture.json"
python3 "${REPO_ROOT}/docker/recovery-state.py" capture "${PORT}" \
  < "${WORK}/recovery-fixture.json" > "${WORK}/recovery-before.json"
BEFORE_USERS="$(apiget "/Users" "${TOKEN}" | jq_py 'sorted(u["Name"] for u in d)')"
pass "distinct users, saved position, favourite, policies, configuration, pack and tokens seeded"

echo "== 3. cold backup: stop the service, archive configuration and data =="
systemctl stop tesserafin
service_stopped && pass "service stopped, no process left under the service account" \
  || { fail "service still running before snapshot"; exit 1; }
state_listing > "${WORK}/state-before.txt"
tar -C / --acls --xattrs -cpzf "${ARCHIVE}" "${STATE_DIRS[@]}"
( cd "${WORK}" && sha256sum "$(basename "${ARCHIVE}")" > "${ARCHIVE}.sha256" )
systemctl start tesserafin
wait_ready && pass "service restarted and ready after backup" || fail "service not ready after backup"

echo "== 3a. backup confidentiality: owner and mode =="
for f in "${ARCHIVE}" "${ARCHIVE}.sha256"; do
  [[ "$(stat -c '%u:%a' "${f}")" == "0:600" ]] && pass "$(basename "${f}") root-owned mode 0600" \
    || fail "$(basename "${f}") is $(stat -c '%u:%a' "${f}"), expected 0:600"
done

echo "== 3e. state loss: purge package, delete persistent directories and account =="
systemctl stop tesserafin
DEBIAN_FRONTEND=noninteractive apt-get purge -y -q tesserafin-server >/dev/null
rm -rf "${ALL_DIRS[@]}"
userdel tesserafin
! getent group tesserafin >/dev/null || groupdel tesserafin
LOST=1
dpkg -s tesserafin-server >/dev/null 2>&1 && LOST=0
getent passwd tesserafin >/dev/null && LOST=0
getent group tesserafin >/dev/null && LOST=0
for d in "${ALL_DIRS[@]}"; do [[ -e "${d}" ]] && LOST=0; done
[[ "$(http_code /health)" == 000 ]] || LOST=0
[[ "${LOST}" == 1 ]] && pass "package purged and all persistent state removed before restore" \
  || { fail "installation not fully removed"; exit 1; }

echo "== 4. restore: verify the snapshot, reinstall the same package, extract =="
( cd "${WORK}" && sha256sum --quiet -c "$(basename "${ARCHIVE}").sha256" ) \
  && pass "snapshot matches its retained checksum" || { fail "snapshot checksum mismatch"; exit 1; }
python3 - "${ARCHIVE}" <<'PY' && pass "archive holds only etc/tesserafin and var/lib/tesserafin entries" \
  || { fail "archive has entries outside the persistent directories"; exit 1; }
import sys, tarfile
roots = ("etc/tesserafin", "var/lib/tesserafin")
with tarfile.open(sys.argv[1]) as t:
    for m in t.getmembers():
        n = m.name.rstrip("/")
        if n.startswith("/") or ".." in n.split("/") or not any(n == r or n.startswith(r + "/") for r in roots) \
                or not (m.isfile() or m.isdir()):
            sys.exit(1)
PY
install_deb
# The package recreates empty state directories and its conffile; the restore replaces them.
[[ -z "$(ls -A /var/lib/tesserafin)" ]] && pass "reinstalled data directory is empty before restore" \
  || fail "reinstalled data directory is not empty"
rm -rf /etc/tesserafin /var/lib/tesserafin
tar -C / --acls --xattrs --same-owner -xpzf "${ARCHIVE}"
state_listing > "${WORK}/state-after.txt"
cmp -s "${WORK}/state-before.txt" "${WORK}/state-after.txt" \
  && pass "restored files identical in content, owner, group and mode" \
  || fail "restored files differ from the pre-backup listing; no listing printed"
[[ "$(stat -c '%U:%G %a' /var/lib/tesserafin /etc/tesserafin /etc/tesserafin/tesserafin.conf | tr '\n' '|')" \
   == "tesserafin:tesserafin 750|tesserafin:tesserafin 750|root:tesserafin 640|" ]] \
  && pass "service directories and environment file keep package ownership and modes" \
  || fail "service directory or environment file ownership/mode differs from the package contract"

echo "== 5. boot restored service + compare =="
systemctl start tesserafin
wait_ready || { diag restored-never-ready; echo "restored server never left the 503 startup phase"; exit 1; }
FIXTURE_ADMIN_TOKEN="$(jq_py 'd["admin_token"]' < "${WORK}/recovery-fixture.json")"
diag restored-ready "${FIXTURE_ADMIN_TOKEN}"
[[ "$(ps -o user= -p "$(systemctl show -P MainPID tesserafin)")" == tesserafin ]] \
  && pass "restored server runs as the tesserafin account" || fail "restored server runs as another account"
if ! python3 "${REPO_ROOT}/docker/recovery-state.py" capture "${PORT}" \
     < "${WORK}/recovery-fixture.json" > "${WORK}/recovery-after.json"; then
  diag restored-capture-failed "${FIXTURE_ADMIN_TOKEN}"
  fail "restored state capture failed"
  exit 1
fi
cmp -s "${WORK}/recovery-before.json" "${WORK}/recovery-after.json" \
  && pass "extended state restored identically by user/item/pack identity" \
  || fail "extended recovery state differs; no private snapshots printed"
AUTH2="$(apipost "/Users/AuthenticateByName" "" "{\"Username\":\"${ADMIN_USER}\",\"Pw\":\"${ADMIN_PASS}\"}")"
TOKEN2="$(echo "${AUTH2}" | jq_py 'd["AccessToken"]')"
USERID2="$(echo "${AUTH2}" | jq_py 'd["User"]["Id"]')"
[[ -n "${TOKEN2}" ]] && pass "restored admin credentials authenticate" || { fail "restored auth failed"; exit 1; }
AFTER_USERS="$(apiget "/Users" "${TOKEN2}" | jq_py 'sorted(u["Name"] for u in d)')"
AFTER_LIBS="$(apiget "/Library/VirtualFolders" "${TOKEN2}" | jq_py 'sorted(f["Name"] for f in d)')"
AFTER_PLAY="$(playstate "${USERID2}" "${ITEMID}" "${TOKEN2}")"
[[ "${AFTER_USERS}" == "${BEFORE_USERS}" ]] && pass "users restored identically" || fail "user set differs"
[[ "${AFTER_LIBS}" == "${BEFORE_LIBS}" ]] && pass "libraries restored identically" || fail "library set differs"
[[ "${AFTER_PLAY}" == "${BEFORE_PLAY}" && "${AFTER_PLAY}" != MISSING ]] \
  && pass "playback state restored (${AFTER_PLAY})" || fail "playback state differs (${AFTER_PLAY} != ${BEFORE_PLAY})"
systemctl stop tesserafin

echo
if [[ "${FAILED}" == 0 ]]; then
  echo "NATIVE-ROUNDTRIP: deb cold snapshot restored and verified"; exit 0
else
  echo "NATIVE-ROUNDTRIP: FAILED"; exit 1
fi
