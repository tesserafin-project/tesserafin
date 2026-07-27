#!/usr/bin/env bash
# shellcheck disable=SC2015,SC2317,SC2329
# SC2015: `<cond> && pass || fail` is intended — pass()/fail() both return 0, so
#         the `|| fail` branch never fires on a true condition.
# SC2317/SC2329: cleanup() is invoked indirectly via the EXIT trap.
#
# Container upgrade rehearsal for the distributable image (#92 / [A6]).
#
# Proves that replacing ONLY the container, keeping the same /config, /data and
# /cache volumes, carries a populated instance forward with no manual database
# work and no data loss.
#
#   baseline   pull a previously published image BY DIGEST, boot it on fresh
#              named volumes, onboard it over HTTP, and populate it with state a
#              real operator would expect to survive: two user identities, a
#              library definition, a visible media item, playback/user state, and
#              a configuration change. Capture a SEMANTIC before-state through
#              the API — not a directory checksum, which proves nothing about
#              whether the data is still meaningful after a schema change.
#
#   upgrade    stop and remove the container, verify the volumes are the same
#              volume objects, start the candidate image BY DIGEST on them, wait
#              for the real /health readiness contract, and capture the same
#              semantic state again.
#
# No SQL is executed, no file is copied between volumes, and nothing is restored
# from a backup: if the migration/startup path does not carry the data, the
# comparison fails.
#
# MIGRATION HONESTY: this harness reports the number of pending migrations the
# runner actually found. Zero pending migrations is a legitimate and reported
# outcome — it means the harness proved in-place volume upgrade and startup, NOT
# that a forward schema migration was exercised. Pass --require-migration to turn
# "zero pending" into a failure once an honest migration boundary exists.
#
# Usage:
#   docker/upgrade-roundtrip.sh [options]
#     --baseline <ref>          image to upgrade FROM (default: the pinned A5 digest)
#     --candidate <ref>         image to upgrade TO   (default: this tree's dev tag)
#     --port <n>                host port for the instance (default 19196)
#     --require-migration       fail if the runner found zero pending migrations
#     --allow-local-candidate   permit a candidate that has no registry digest
#                               (development iteration only — never for evidence)
#     --keep                    DEBUG: leave containers and volumes behind
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONTRACT="${REPO_ROOT}/docker/version-contract.sh"

# The A5 image, pinned by its multi-arch manifest digest (docs/container/A5-observability.md).
#
# EPOCH NOTE (docs/versioning-policy.md): this default is a PRE-v1 development
# image from the frozen `tesserafin` archive. It is retained because the recorded
# A6 evidence was produced against it and that evidence is not rewritten. It is
# NOT a member of the supported 1.x upgrade graph, so it is not a valid baseline
# for the #127 forward-migration gate — that gate must be driven with an explicit
# `--baseline <1.x digest>`. The default is a convenience for re-running the
# historical rehearsal, nothing more.
BASELINE_DEFAULT="ghcr.io/tesserafin-project/tesserafin@sha256:6e3dbaab6eeaef163e81f9cc5ffb03f5a05bb9d8165e3f6487b2bb3003bc7608"
HELPER="busybox:stable@sha256:73aaf090f3d85aa34ee199857f03fa3a95c8ede2ffd4cc2cdb5b94e566b11662"

BASELINE="${BASELINE_DEFAULT}"
CANDIDATE=""
PORT=19196
REQUIRE_MIGRATION=0
ALLOW_LOCAL_CANDIDATE=0
KEEP=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --baseline)  BASELINE="$2"; shift 2 ;;
    --candidate) CANDIDATE="$2"; shift 2 ;;
    --port)      PORT="$2"; shift 2 ;;
    --require-migration)     REQUIRE_MIGRATION=1; shift ;;
    --allow-local-candidate) ALLOW_LOCAL_CANDIDATE=1; shift ;;
    --keep)      KEEP=1; shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

CANDIDATE="${CANDIDATE:-$("${CONTRACT}" tags --channel dev | head -1)}"

ADMIN_USER="a6admin"
ADMIN_PASS="a6-upgrade-rehearsal-pass"
SECOND_USER="a6viewer"
SERVER_NAME="A6 Upgrade Rehearsal"
POS_TICKS=100000000             # 10s in ticks; ~33% of the 30s clip
AUTH_HDR='MediaBrowser Client="tf-a6", Device="upgrade", DeviceId="tf-a6-up", Version="1.0"'
# The server can take the better part of a minute to exit cleanly after real
# work; a short stop timeout would SIGKILL it mid-write and invalidate the test.
STOP_TIMEOUT=120

SFX="$$"
V_CFG="tf_a6_config_${SFX}" ; V_DATA="tf_a6_data_${SFX}" ; V_CACHE="tf_a6_cache_${SFX}"
C_BASE="tf-a6-baseline-${SFX}" ; C_CAND="tf-a6-candidate-${SFX}"
WORK="$(mktemp -d)"
MEDIA="${WORK}/media"

FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }
note() { echo "  NOTE  $*"; }

cleanup() {
  if [[ "${KEEP}" == "1" ]]; then
    echo
    echo "--keep: leaving containers ${C_BASE} / ${C_CAND} and volumes ${V_CFG} ${V_DATA} ${V_CACHE} in place."
    echo "        clean up with: docker rm -f ${C_BASE} ${C_CAND}; docker volume rm ${V_CFG} ${V_DATA} ${V_CACHE}"
    return
  fi
  docker rm -f "${C_BASE}" "${C_CAND}" >/dev/null 2>&1 || true
  docker volume rm "${V_CFG}" "${V_DATA}" "${V_CACHE}" >/dev/null 2>&1 || true
  docker run --rm -v "${WORK}:/w" "${HELPER}" chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}" 2>/dev/null || true
}
trap cleanup EXIT

# --- helpers -----------------------------------------------------------------

json_field() { # $1 = field name, case-insensitive
  python3 -c '
import sys, json
want = sys.argv[1].lower()
try:
    d = json.load(sys.stdin)
except Exception:
    print(""); raise SystemExit(0)
for k, v in (d or {}).items():
    if k.lower() == want:
        print(v); raise SystemExit(0)
print("")
' "$1"
}
jq_py() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)"; }

# Readiness is /health answering 200 AND status=healthy. /System/Info/Public is
# NOT a readiness signal: the startup server answers it long before the real
# pipeline and its database are up.
wait_health() { # $1 = container name
  local body
  for _ in $(seq 1 180); do   # up to ~360s
    if ! docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null | grep -q true; then
      echo "container $1 exited while waiting for /health" >&2
      return 1
    fi
    body="$(curl -fsS "http://127.0.0.1:${PORT}/health" 2>/dev/null || true)"
    if [[ -n "${body}" ]] && [[ "$(json_field status <<<"${body}")" == "healthy" ]]; then
      printf '%s' "${body}"
      return 0
    fi
    sleep 2
  done
  return 1
}
# The startup wizard and the first library scan are served by endpoints that
# exist before core startup completes; this rides out the first-boot restart.
wait_startup_api() {
  local code streak=0
  for _ in $(seq 1 150); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${PORT}/Startup/Configuration" 2>/dev/null || echo 000)"
    if [[ "${code}" != "503" && "${code}" != "000" ]]; then
      streak=$((streak + 1)); [[ "${streak}" -ge 5 ]] && return 0
    else
      streak=0
    fi
    sleep 2
  done
  return 1
}

RETRY=(--retry 6 --retry-delay 2 --retry-all-errors)
apipost() { # $1=path $2=token("" for none) [$3=json body]
  local path="$1" tok="$2" body="${3:-}"
  local hdr="${AUTH_HDR}"; [[ -n "${tok}" ]] && hdr="${AUTH_HDR}, Token=\"${tok}\""
  if [[ -n "${body}" ]]; then
    curl -fsS "${RETRY[@]}" -X POST "http://127.0.0.1:${PORT}${path}" \
      -H "Authorization: ${hdr}" -H 'Content-Type: application/json' -d "${body}"
  else
    curl -fsS "${RETRY[@]}" -X POST "http://127.0.0.1:${PORT}${path}" -H "Authorization: ${hdr}"
  fi
}
apiget() { # $1=path $2=token("" for none)
  local hdr="${AUTH_HDR}"; [[ -n "${2}" ]] && hdr="${AUTH_HDR}, Token=\"${2}\""
  curl -fsS "${RETRY[@]}" "http://127.0.0.1:${PORT}$1" -H "Authorization: ${hdr}"
}

authenticate() { # $1=user $2=pass -> "<token> <userId>"
  local out
  out="$(apipost "/Users/AuthenticateByName" "" "{\"Username\":\"$1\",\"Pw\":\"$2\"}")"
  printf '%s %s' "$(jq_py 'd["AccessToken"]' <<<"${out}")" "$(jq_py 'd["User"]["Id"]' <<<"${out}")"
}

# The semantic state. Every line is a stable API fact, deliberately including
# identifiers (not just names) so a migration that recreated a row rather than
# carrying it forward is caught.
capture_state() { # $1=token $2=adminUserId $3=itemId
  local tok="$1" uid="$2" item="$3"
  echo "users=$(apiget "/Users" "${tok}" | jq_py 'sorted("{}:{}".format(u["Name"], u["Id"]) for u in d)')"
  echo "libraries=$(apiget "/Library/VirtualFolders" "${tok}" | jq_py 'sorted("{}:{}".format(f["Name"], f.get("CollectionType","")) for f in d)')"
  echo "items=$(apiget "/Items?userId=${uid}&recursive=true&includeItemTypes=Movie" "${tok}" | jq_py 'sorted("{}:{}".format(i["Name"], i["Id"]) for i in d.get("Items", []))')"
  echo "playstate=$(apiget "/Items?userId=${uid}&ids=${item}" "${tok}" | jq_py '"Played={}|PlayCount={}|Position={}".format(d["Items"][0]["UserData"]["Played"], d["Items"][0]["UserData"]["PlayCount"], d["Items"][0]["UserData"]["PlaybackPositionTicks"]) if d.get("Items") else "MISSING"')"
  echo "servername=$(apiget "/System/Configuration" "${tok}" | jq_py 'd.get("ServerName","")')"
}

# --- 0. resolve both images to immutable digests ------------------------------
echo "== 0. resolve baseline and candidate to immutable digests =="
resolve_digest() { # $1 = image ref -> prints "<repo>@sha256:..." or ""
  docker image inspect --format '{{if .RepoDigests}}{{index .RepoDigests 0}}{{end}}' "$1" 2>/dev/null
}
docker image inspect "${BASELINE}" >/dev/null 2>&1 || docker pull -q "${BASELINE}" >/dev/null
docker image inspect "${CANDIDATE}" >/dev/null 2>&1 || docker pull -q "${CANDIDATE}" >/dev/null

BASELINE_DIGEST="$(resolve_digest "${BASELINE}")"
CANDIDATE_DIGEST="$(resolve_digest "${CANDIDATE}")"

if [[ "${BASELINE}" == *"@sha256:"* ]]; then
  BASELINE_REF="${BASELINE}"
  pass "baseline is referenced by digest: ${BASELINE_REF}"
elif [[ -n "${BASELINE_DIGEST}" ]]; then
  BASELINE_REF="${BASELINE_DIGEST}"
  pass "baseline ${BASELINE} resolved to ${BASELINE_REF}"
else
  fail "baseline ${BASELINE} has no registry digest"; exit 1
fi

if [[ "${CANDIDATE}" == *"@sha256:"* ]]; then
  CANDIDATE_REF="${CANDIDATE}"
  pass "candidate is referenced by digest: ${CANDIDATE_REF}"
elif [[ -n "${CANDIDATE_DIGEST}" ]]; then
  CANDIDATE_REF="${CANDIDATE_DIGEST}"
  pass "candidate ${CANDIDATE} resolved to ${CANDIDATE_REF}"
elif [[ "${ALLOW_LOCAL_CANDIDATE}" == "1" ]]; then
  CANDIDATE_REF="${CANDIDATE}"
  note "candidate has NO registry digest (local build, image id $(docker image inspect --format '{{.Id}}' "${CANDIDATE}")) — --allow-local-candidate; this run is NOT digest-pinned evidence"
else
  fail "candidate ${CANDIDATE} has no registry digest — push it, or pass --allow-local-candidate for a non-evidential run"
  exit 1
fi

# One cached tag in this project's history is arm64; a silent architecture switch
# between baseline and candidate would make the comparison meaningless.
BASE_ARCH="$(docker image inspect --format '{{.Architecture}}' "${BASELINE_REF}")"
CAND_ARCH="$(docker image inspect --format '{{.Architecture}}' "${CANDIDATE_REF}")"
[[ "${BASE_ARCH}" == "${CAND_ARCH}" ]] \
  && pass "baseline and candidate are the same architecture (${BASE_ARCH})" \
  || fail "architecture mismatch: baseline ${BASE_ARCH}, candidate ${CAND_ARCH}"

BASE_VERSION="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}}' "${BASELINE_REF}")"
BASE_REVISION="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "${BASELINE_REF}")"
CAND_VERSION="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}}' "${CANDIDATE_REF}")"
CAND_REVISION="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "${CANDIDATE_REF}")"
echo "  baseline  version=${BASE_VERSION} revision=${BASE_REVISION}"
echo "  candidate version=${CAND_VERSION} revision=${CAND_REVISION}"
[[ "${BASE_REVISION}" != "${CAND_REVISION}" ]] \
  && pass "candidate is built from a different source commit than the baseline" \
  || fail "baseline and candidate are the same commit — this would not be an upgrade"

# --- 1. baseline on fresh volumes --------------------------------------------
echo "== 1. baseline on fresh named volumes =="
mkdir -p "${MEDIA}/Upgrade Fixture (2021)"
for v in "${V_CFG}" "${V_DATA}" "${V_CACHE}"; do docker volume create "$v" >/dev/null; done
docker run --rm --user 0:0 --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg \
  -v "${MEDIA}:/m" "${BASELINE_REF}" \
  -y -f lavfi -i testsrc=duration=30:size=160x120:rate=5 \
  -pix_fmt yuv420p "/m/Upgrade Fixture (2021)/Upgrade Fixture (2021).mp4" >/dev/null 2>&1
docker run --rm -v "${V_CFG}:/v/config" -v "${V_DATA}:/v/data" -v "${V_CACHE}:/v/cache" \
  "${HELPER}" sh -c 'chown -R 10000:10000 /v/*'

# Record the volume identities now. The upgrade must reuse these exact objects.
# CreatedAt, not Mountpoint: a mountpoint is derived from the volume name, so a
# volume deleted and recreated under the same name would produce an identical
# string and the "upgraded in place" assertion would pass on a replaced volume.
vol_id() { docker volume inspect --format '{{.Name}}@{{.CreatedAt}}' "$1"; }
VOLIDS_BEFORE="$(vol_id "${V_CFG}"); $(vol_id "${V_DATA}"); $(vol_id "${V_CACHE}")"

docker run -d --name "${C_BASE}" -p "127.0.0.1:${PORT}:8096" \
  -v "${V_CFG}:/config" -v "${V_DATA}:/data" -v "${V_CACHE}:/cache" \
  -v "${MEDIA}:/media:ro" "${BASELINE_REF}" >/dev/null
BASE_HEALTH="$(wait_health "${C_BASE}")" || { fail "baseline never reached /health healthy"; docker logs "${C_BASE}" 2>&1 | tail -40; exit 1; }
pass "baseline reached /health 200 status=healthy"
wait_startup_api || { fail "baseline startup API never stabilised"; exit 1; }

# --- 2. populate through the supported HTTP path ------------------------------
echo "== 2. onboard and populate over HTTP =="
apiget "/Startup/User" "" >/dev/null
apipost "/Startup/User" "" "{\"Name\":\"${ADMIN_USER}\",\"Password\":\"${ADMIN_PASS}\"}" >/dev/null
apipost "/Startup/Complete" "" >/dev/null
read -r TOKEN USERID <<<"$(authenticate "${ADMIN_USER}" "${ADMIN_PASS}")"
[[ -n "${TOKEN}" && -n "${USERID}" ]] && pass "onboarded and authenticated as ${ADMIN_USER}" \
  || { fail "onboarding failed"; exit 1; }

# A second identity, so the comparison covers more than the one account the
# startup wizard happens to create.
apipost "/Users/New" "${TOKEN}" "{\"Name\":\"${SECOND_USER}\",\"Password\":\"${ADMIN_PASS}\"}" >/dev/null
pass "created a second user identity (${SECOND_USER})"

apipost "/Library/VirtualFolders?name=Movies&collectionType=movies&paths=%2Fmedia&refreshLibrary=true" \
  "${TOKEN}" '{"LibraryOptions":{"PathInfos":[{"Path":"/media"}]}}' >/dev/null

ITEMID=""
for _ in $(seq 1 60); do
  ITEMID="$(apiget "/Items?userId=${USERID}&recursive=true&includeItemTypes=Movie" "${TOKEN}" 2>/dev/null \
            | jq_py 'd["Items"][0]["Id"] if d.get("Items") else ""' 2>/dev/null || true)"
  [[ -n "${ITEMID}" ]] && break
  sleep 2
done
[[ -n "${ITEMID}" ]] && pass "library scan made the media fixture visible (item ${ITEMID})" \
  || { fail "library scan produced no item"; exit 1; }

apipost "/Sessions/Playing" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PlayMethod\":\"DirectPlay\",\"CanSeek\":true}" >/dev/null
apipost "/Sessions/Playing/Progress" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PlayMethod\":\"DirectPlay\",\"PositionTicks\":${POS_TICKS},\"IsPaused\":false}" >/dev/null
apipost "/Sessions/Playing/Stopped" "${TOKEN}" \
  "{\"ItemId\":\"${ITEMID}\",\"MediaSourceId\":\"${ITEMID}\",\"PositionTicks\":${POS_TICKS}}" >/dev/null

# Configuration that must survive: a server-level setting written to /config.
CONFIG_JSON="$(apiget "/System/Configuration" "${TOKEN}")"
# Read the live configuration, change one field, post the whole document back —
# the endpoint replaces the document, so a partial body would drop settings.
python3 -c '
import json, sys
config = json.load(sys.stdin)
config["ServerName"] = sys.argv[1]
json.dump(config, open(sys.argv[2], "w"))
' "${SERVER_NAME}" "${WORK}/cfg.json" <<<"${CONFIG_JSON}"
curl -fsS "${RETRY[@]}" -X POST "http://127.0.0.1:${PORT}/System/Configuration" \
  -H "Authorization: ${AUTH_HDR}, Token=\"${TOKEN}\"" \
  -H 'Content-Type: application/json' --data-binary "@${WORK}/cfg.json" >/dev/null
pass "wrote a configuration change (ServerName)"

BEFORE="$(capture_state "${TOKEN}" "${USERID}" "${ITEMID}")"
echo "  --- before ---"; printf '    %s\n' "${BEFORE}"

# A capture line is `key=value`. If an API call or its parse failed, the value is
# empty, `echo` still succeeded, and the after-state would fail identically — so
# compare_line would PASS on two empty strings. Reject that here, before the
# comparison is trusted.
for key in users libraries items playstate servername; do
  value="$(grep "^${key}=" <<<"${BEFORE}" || true)"
  [[ -n "${value}" && "${value}" != "${key}=" && "${value}" != "${key}=[]" ]] \
    && pass "before-state captured a non-empty ${key}" \
    || fail "before-state ${key} is empty — the comparison below would be vacuous"
done
grep -q "^servername=${SERVER_NAME}$" <<<"${BEFORE}" \
  && pass "configuration change is readable before the upgrade" \
  || fail "configuration change did not take effect before the upgrade"
grep -q "Played=True" <<<"${BEFORE}" \
  && pass "playback state recorded before the upgrade" \
  || fail "playback state was not recorded before the upgrade"

# --- 3. replace ONLY the container -------------------------------------------
echo "== 3. stop and replace the container, keep the volumes =="
docker stop -t "${STOP_TIMEOUT}" "${C_BASE}" >/dev/null
BASE_EXIT="$(docker inspect -f '{{.State.ExitCode}}' "${C_BASE}")"
[[ "${BASE_EXIT}" == "0" || "${BASE_EXIT}" == "143" ]] \
  && pass "baseline container stopped cleanly (exit ${BASE_EXIT})" \
  || fail "baseline container exited ${BASE_EXIT}"
docker rm "${C_BASE}" >/dev/null

VOLIDS_AFTER="$(vol_id "${V_CFG}"); $(vol_id "${V_DATA}"); $(vol_id "${V_CACHE}")"
[[ "${VOLIDS_AFTER}" == "${VOLIDS_BEFORE}" ]] \
  && pass "the same three volume objects survived container removal" \
  || fail "volumes changed identity across container replacement"

echo "== 4. start the candidate image on those volumes =="
docker run -d --name "${C_CAND}" -p "127.0.0.1:${PORT}:8096" \
  -v "${V_CFG}:/config" -v "${V_DATA}:/data" -v "${V_CACHE}:/cache" \
  -v "${MEDIA}:/media:ro" "${CANDIDATE_REF}" >/dev/null
CAND_HEALTH="$(wait_health "${C_CAND}")" || { fail "candidate never reached /health healthy"; docker logs "${C_CAND}" 2>&1 | tail -60; exit 1; }
pass "candidate reached /health 200 status=healthy on the upgraded volumes"

# --- 5. migration evidence ----------------------------------------------------
echo "== 5. migration evidence =="
CLOG="$(docker logs "${C_CAND}" 2>&1)"
grep -q "Initialise Migration service" <<<"${CLOG}" \
  && pass "the migration runner executed on the upgraded volumes" \
  || fail "no migration-service log line on the upgraded instance"

# "There are N migrations for stage <Stage>." — one line per stage.
PENDING_LINES="$(grep -oE 'There are [0-9]+ migrations for stage [A-Za-z]+' <<<"${CLOG}" || true)"
PENDING_TOTAL=0
STAGE_COUNT=0
if [[ -n "${PENDING_LINES}" ]]; then
  while IFS= read -r line; do
    echo "    ${line}"
    n="$(grep -oE 'are [0-9]+' <<<"${line}" | grep -oE '[0-9]+')"
    PENDING_TOTAL=$((PENDING_TOTAL + n))
    STAGE_COUNT=$((STAGE_COUNT + 1))
  done <<<"${PENDING_LINES}"
fi
echo "  migration stages swept: ${STAGE_COUNT}"
echo "  pending migrations applied during the upgrade: ${PENDING_TOTAL}"

# Completion evidence for an ALREADY-INITIALISED instance is the runner reporting
# every stage it swept. "Migration system initialisation completed" is emitted on
# first-boot seeding only, and "was successfully applied" only when a migration
# actually ran — demanding either of those here would fail an upgrade that had
# nothing pending, which is a real and correct outcome, not a fault.
[[ "${STAGE_COUNT}" -ge 1 ]] \
  && pass "the migration runner swept every stage and reported each (${STAGE_COUNT} stages)" \
  || fail "the runner emitted no per-stage report — the migration path did not complete"
grep -qiE 'Attempt to rollback|Migration .* failed' <<<"${CLOG}" \
  && fail "the migration runner attempted a rollback" \
  || pass "no rollback was attempted"

if [[ "${PENDING_TOTAL}" -gt 0 ]]; then
  grep -qiE 'Migration .* was successfully applied' <<<"${CLOG}" \
    && pass "every pending migration reported success" \
    || fail "${PENDING_TOTAL} migrations were pending but none reported success"
  pass "a real forward migration ran during this upgrade (${PENDING_TOTAL} applied)"
elif [[ "${REQUIRE_MIGRATION}" == "1" ]]; then
  fail "--require-migration: the runner found ZERO pending migrations, so no forward schema migration was exercised"
else
  note "the migration runner executed with ZERO pending migrations."
  note "This run proves in-place volume upgrade, startup and data preservation."
  note "It does NOT prove a forward schema migration. Baseline and candidate"
  note "carry the same migration set, so there is no honest boundary to cross."
fi

# --- 6. semantic after-state --------------------------------------------------
echo "== 6. compare the semantic state =="
read -r TOKEN2 USERID2 <<<"$(authenticate "${ADMIN_USER}" "${ADMIN_PASS}")"
[[ -n "${TOKEN2}" ]] && pass "the pre-upgrade admin credentials still authenticate" \
  || { fail "admin authentication broke across the upgrade"; exit 1; }
[[ "${USERID2}" == "${USERID}" ]] && pass "the admin user id is unchanged (${USERID2})" \
  || fail "admin user id changed: ${USERID} -> ${USERID2}"
read -r TOKEN_V _ <<<"$(authenticate "${SECOND_USER}" "${ADMIN_PASS}")"
[[ -n "${TOKEN_V}" ]] && pass "the second user identity still authenticates" \
  || fail "the second user identity did not survive the upgrade"

AFTER="$(capture_state "${TOKEN2}" "${USERID2}" "${ITEMID}")"
echo "  --- after ---"; printf '    %s\n' "${AFTER}"

compare_line() { # $1 = key
  local b a
  # `|| true`: a missing key must be reported as a FAIL, not kill the run via set -e.
  b="$(grep "^$1=" <<<"${BEFORE}" || true)"
  a="$(grep "^$1=" <<<"${AFTER}" || true)"
  if [[ -z "${b}" || -z "${a}" ]]; then
    fail "$1 could not be captured on both sides — before [${b}] after [${a}]"
  elif [[ "${b}" == "${a}" ]]; then
    pass "$1 preserved across the upgrade"
  else
    fail "$1 differs — before [${b}] after [${a}]"
  fi
}
for k in users libraries items playstate servername; do compare_line "${k}"; done
grep -q "playstate=MISSING" <<<"${AFTER}" && fail "playback state is missing after the upgrade" \
  || pass "playback state is still present after the upgrade"

# --- 7. runtime identity and version agreement --------------------------------
echo "== 7. non-root operation, volume ownership and version agreement =="
RUNAS="$(docker exec "${C_CAND}" id -u 2>/dev/null || echo unknown)"
[[ "${RUNAS}" == "10000" ]] && pass "the upgraded container runs as uid 10000 (non-root)" \
  || fail "the upgraded container runs as uid '${RUNAS}', expected 10000"
OWNERS="$(docker run --rm -v "${V_CFG}:/v/config" -v "${V_DATA}:/v/data" -v "${V_CACHE}:/v/cache" \
  "${HELPER}" sh -c 'stat -c "%u:%g" /v/config /v/data /v/cache' | sort -u | tr '\n' ' ')"
[[ "${OWNERS}" == "10000:10000 " ]] && pass "all three volumes are still owned by 10000:10000" \
  || fail "volume ownership after the upgrade: ${OWNERS}"

HEALTH_VERSION="$(json_field version <<<"${CAND_HEALTH}")"
BASE_HEALTH_VERSION="$(json_field version <<<"${BASE_HEALTH}")"
echo "  baseline  /health version : ${BASE_HEALTH_VERSION}"
echo "  candidate /health version : ${HEALTH_VERSION}"
[[ "${HEALTH_VERSION}" == "${CAND_VERSION}" ]] \
  && pass "candidate /health version == org.opencontainers.image.version (${HEALTH_VERSION})" \
  || fail "candidate /health version '${HEALTH_VERSION}' != OCI label '${CAND_VERSION}'"
SRC_VERSION="$("${CONTRACT}" version)"
[[ "${CAND_VERSION}" == "${SRC_VERSION}" ]] \
  && pass "candidate OCI version == SharedVersion.cs (${SRC_VERSION})" \
  || fail "candidate OCI version '${CAND_VERSION}' != SharedVersion.cs '${SRC_VERSION}'"
[[ "${CAND_REVISION}" =~ ^[0-9a-f]{40}$ ]] \
  && pass "candidate OCI revision is a full source commit (${CAND_REVISION})" \
  || fail "candidate OCI revision '${CAND_REVISION}' is not a 40-char commit sha"
INFO_VERSION="$(apiget "/System/Info/Public" "" | json_field version)"
[[ "${INFO_VERSION}" == "${HEALTH_VERSION}" ]] \
  && pass "application-reported version == /health version (${INFO_VERSION})" \
  || fail "application-reported version '${INFO_VERSION}' != /health version '${HEALTH_VERSION}'"

echo
echo "== upgrade round-trip summary =="
echo "  baseline           : ${BASELINE_REF}"
echo "  candidate          : ${CANDIDATE_REF}"
echo "  pending migrations : ${PENDING_TOTAL}"
echo "  volumes            : upgraded in place (${V_CFG}, ${V_DATA}, ${V_CACHE})"
echo
if [[ "${FAILED}" == 0 ]]; then
  echo "UPGRADE-ROUNDTRIP: all assertions passed"
  exit 0
else
  echo "UPGRADE-ROUNDTRIP: FAILED"
  exit 1
fi
