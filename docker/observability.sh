#!/usr/bin/env bash
# Observability gate for the Tesserafin production image (#91 / [A5]).
#
# Proves, against a real running container and its real embedded SQLite database:
#   1. GET /health answers 200 application/json with the stable schema, the exact
#      application version and an explicit healthy database status.
#   2. Cache-Control: no-store is set.
#   3. EVERY application log line on stdout parses as JSON.
#   4. The A4 hardware-selection decision survives as structured FIELDS, not prose.
#   5. TESSERAFIN_LOG_LEVEL changes which events are emitted, without a rebuild.
#   6. An invalid TESSERAFIN_LOG_LEVEL does not crash the server and produces a
#      valid structured warning.
#
# What it deliberately does NOT do: stop a database process. Tesserafin's database
# is embedded SQLite opened in-process — there is nothing to stop. The failing-probe
# side of the contract is proven over HTTP in
# tests/Tesserafin.Server.Integration.Tests/HealthEndpointTests.cs.
#
# Usage: docker/observability.sh <image-ref> [host-port]
set -euo pipefail

IMAGE="${1:?usage: observability.sh <image-ref> [host-port]}"
PORT="${2:-18097}"
# Boot budget. 120s suits native hardware; raise it for emulated arm64 runs.
BOOT_TIMEOUT="${BOOT_TIMEOUT:-120}"

WORK="$(mktemp -d)"
CNAME="tesserafin-observability-$$"
FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }

cleanup() {
  docker rm -f "${CNAME}" >/dev/null 2>&1 || true
  docker run --rm -v "${WORK}:/w" busybox chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}"
}
trap cleanup EXIT

prepare_state() {
  # Stop any previous run first, then hand the volumes back from the container's
  # uid 10000 so the host user can actually delete them (no host sudo needed).
  docker rm -f "${CNAME}" >/dev/null 2>&1 || true
  if [[ -d "${WORK}/config" ]]; then
    docker run --rm -v "${WORK}:/w" busybox chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  fi
  rm -rf "${WORK}/config" "${WORK}/cache" "${WORK}/data" "${WORK}/media"
  mkdir -p "${WORK}/config" "${WORK}/cache" "${WORK}/data" "${WORK}/media"
  docker run --rm -v "${WORK}:/w" busybox chown -R 10000:10000 /w/config /w/cache /w/data
}

# start_container [env KEY=VALUE ...]
start_container() {
  local env_args=()
  local kv
  for kv in "$@"; do env_args+=(-e "${kv}"); done
  docker rm -f "${CNAME}" >/dev/null 2>&1 || true
  docker run -d --name "${CNAME}" \
    -p "127.0.0.1:${PORT}:8096" \
    -v "${WORK}/config:/config" \
    -v "${WORK}/cache:/cache" \
    -v "${WORK}/data:/data" \
    -v "${WORK}/media:/media:ro" \
    "${env_args[@]}" \
    "${IMAGE}" >/dev/null
}

# Waits until /health reports the fully started server, i.e. status=healthy. While
# the startup server still owns the route it answers 503 with status=starting, so
# this is a readiness wait and not a race.
wait_for_healthy() {
  local deadline=$(( SECONDS + BOOT_TIMEOUT )) body
  : > "${WORK}/health-observed.txt"
  while (( SECONDS < deadline )); do
    # EVERY observed body is recorded, not just the final one. The contract is that
    # /health answers the same JSON shape from its first response onwards; a body that
    # is only JSON once the server is ready is a broken contract, because a probe hits
    # this endpoint precisely while the server is NOT ready. Regression guard: the
    # startup-message middleware used to swallow /health and answer plain-text HTML.
    body="$(curl -s "http://127.0.0.1:${PORT}/health" 2>/dev/null || true)"
    if [[ -n "${body}" ]]; then
      printf '%s\n' "${body}" >> "${WORK}/health-observed.txt"
    fi
    if [[ "$(printf '%s' "${body}" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("status",""))' 2>/dev/null)" == "healthy" ]]; then
      return 0
    fi
    if ! docker ps -q --filter "name=${CNAME}" | grep -q .; then
      echo "  container exited early; logs:"; docker logs "${CNAME}" 2>&1 | tail -30
      return 1
    fi
    sleep 2
  done
  return 1
}

# Every line on stdout must be a JSON object. stderr is excluded on purpose: the
# only writer there is the pre-logger bootstrap failure path in StartupHelpers,
# which by definition runs before any logging framework exists.
assert_stdout_is_json_lines() {
  local label="$1"
  docker logs "${CNAME}" 2>/dev/null > "${WORK}/stdout.log"
  local total bad
  total="$(wc -l < "${WORK}/stdout.log")"
  bad="$(python3 - "${WORK}/stdout.log" <<'PY'
import json, sys
bad = []
with open(sys.argv[1], encoding="utf-8", errors="replace") as handle:
    for number, line in enumerate(handle, 1):
        line = line.strip()
        if not line:
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            bad.append((number, line))
            continue
        if not isinstance(value, dict):
            bad.append((number, line))
for number, line in bad[:5]:
    print(f"{number}: {line[:200]}")
print(f"__COUNT__{len(bad)}")
PY
)"
  local count
  count="$(sed -n 's/^__COUNT__//p' <<<"${bad}")"
  if [[ "${count}" == "0" && "${total}" -gt 0 ]]; then
    pass "${label}: all ${total} stdout lines parse as JSON objects"
  else
    fail "${label}: ${count} of ${total} stdout lines are not JSON"
    grep -v '^__COUNT__' <<<"${bad}" | sed 's/^/    /'
  fi
}

# Counts events at a given level in the captured stdout.
count_level() {
  python3 - "${WORK}/stdout.log" "$1" <<'PY'
import json, sys
wanted = sys.argv[2]
total = 0
with open(sys.argv[1], encoding="utf-8", errors="replace") as handle:
    for line in handle:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(event, dict) and event.get("level") == wanted:
            total += 1
print(total)
PY
}

echo "== 1/4 default container logging + health =="
prepare_state
start_container
if ! wait_for_healthy; then
  fail "server never reported status=healthy within ${BOOT_TIMEOUT}s"
  echo "SMOKE: FAILURES present"; exit 1
fi

# Every /health body seen from boot onwards, including the not-ready ones.
if python3 - "${WORK}/health-observed.txt" <<'PY'
import json, sys
allowed = {"healthy", "starting", "unhealthy"}
seen = 0
with open(sys.argv[1], encoding="utf-8", errors="replace") as handle:
    for number, line in enumerate(handle, 1):
        line = line.strip()
        if not line:
            continue
        seen += 1
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            print(f"    line {number} is not JSON: {line[:160]}")
            sys.exit(1)
        if not isinstance(event, dict) or event.get("status") not in allowed:
            print(f"    line {number} is not the health contract: {line[:160]}")
            sys.exit(1)
        for field in ("status", "version", "database"):
            if field not in event:
                print(f"    line {number} is missing '{field}': {line[:160]}")
                sys.exit(1)
print(f"    {seen} observed /health bodies, all on contract")
sys.exit(0 if seen else 1)
PY
then
  pass "every /health body from boot onwards is the same JSON contract"
else
  fail "/health answered something other than its JSON contract before becoming ready"
fi

HEALTH_HEADERS="$(curl -s -D - -o "${WORK}/health.json" -w '%{http_code}' "http://127.0.0.1:${PORT}/health")"
HEALTH_CODE="${HEALTH_HEADERS##*$'\n'}"
echo "  /health -> ${HEALTH_CODE}"
cat "${WORK}/health.json"; echo
[[ "${HEALTH_CODE}" == "200" ]] && pass "/health returns 200" || fail "/health returned ${HEALTH_CODE}"

HEALTH_CT="$(curl -s -o /dev/null -w '%{content_type}' "http://127.0.0.1:${PORT}/health")"
[[ "${HEALTH_CT}" == application/json* ]] && pass "/health is application/json" || fail "/health content-type is '${HEALTH_CT}'"

if curl -s -D - -o /dev/null "http://127.0.0.1:${PORT}/health" | grep -qi '^cache-control:.*no-store'; then
  pass "/health sets Cache-Control: no-store"
else
  fail "/health does not set Cache-Control: no-store"
fi

if python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); sys.exit(0 if d.get("status")=="healthy" and d.get("database")=="healthy" and d.get("version") else 1)' "${WORK}/health.json"; then
  pass "/health body has status=healthy, database=healthy and a version"
else
  fail "/health body does not satisfy the schema"
fi

# The version must be the running server's own, not a hard-coded literal here.
HEALTH_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "${WORK}/health.json")"
INFO_VERSION="$(curl -fsS "http://127.0.0.1:${PORT}/System/Info/Public" | python3 -c 'import json,sys; d=json.load(sys.stdin); print({k.lower():v for k,v in d.items()}.get("version",""))')"
echo "  /health version=${HEALTH_VERSION}  /System/Info/Public version=${INFO_VERSION}"
[[ -n "${HEALTH_VERSION}" && "${HEALTH_VERSION}" == "${INFO_VERSION}" ]] \
  && pass "/health reports the exact application version" \
  || fail "/health version '${HEALTH_VERSION}' disagrees with /System/Info/Public '${INFO_VERSION}'"

# A 404 would mean the endpoint is gated behind authentication for anonymous callers.
ANON_CODE="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${PORT}/health")"
[[ "${ANON_CODE}" == "200" ]] && pass "/health is reachable without authentication" || fail "anonymous /health returned ${ANON_CODE}"

assert_stdout_is_json_lines "default"

echo "== 2/4 A4 hardware-selection event stays structured =="
if python3 - "${WORK}/stdout.log" <<'PY'
import json, sys
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    line = line.strip()
    if not line:
        continue
    try:
        event = json.loads(line)
    except json.JSONDecodeError:
        continue
    if isinstance(event, dict) and "Mode" in event and "Backend" in event and "Reason" in event:
        print(f"    Mode={event['Mode']} Backend={event['Backend']} Reason={event['Reason']}")
        sys.exit(0)
sys.exit(1)
PY
then
  pass "A4 selection decision present as structured fields (Mode/Backend/Reason)"
else
  fail "A4 selection decision is not present as structured fields"
fi

DEFAULT_DEBUG="$(count_level Debug)"
DEFAULT_INFO="$(count_level Information)"
echo "  default level counts: Information=${DEFAULT_INFO} Debug=${DEFAULT_DEBUG}"
[[ "${DEFAULT_INFO}" -gt 0 ]] && pass "Information events are emitted by default" || fail "no Information events at the default level"
[[ "${DEFAULT_DEBUG}" == "0" ]] && pass "Debug events are suppressed by default" || fail "Debug events leak at the default level"

echo "== 3/4 TESSERAFIN_LOG_LEVEL is switchable without a rebuild =="
prepare_state
start_container "TESSERAFIN_LOG_LEVEL=Debug"
if wait_for_healthy; then
  assert_stdout_is_json_lines "TESSERAFIN_LOG_LEVEL=Debug"
  DEBUG_DEBUG="$(count_level Debug)"
  echo "  Debug-level counts: Debug=${DEBUG_DEBUG}"
  [[ "${DEBUG_DEBUG}" -gt 0 ]] && pass "TESSERAFIN_LOG_LEVEL=Debug emits Debug events" || fail "TESSERAFIN_LOG_LEVEL=Debug emitted no Debug events"
else
  fail "server did not become healthy with TESSERAFIN_LOG_LEVEL=Debug"
fi

echo "== 4/4 an invalid TESSERAFIN_LOG_LEVEL degrades safely =="
prepare_state
start_container "TESSERAFIN_LOG_LEVEL=loud"
if wait_for_healthy; then
  pass "server still starts and reports healthy with an invalid log level"
  assert_stdout_is_json_lines "TESSERAFIN_LOG_LEVEL=loud"
  if python3 - "${WORK}/stdout.log" <<'PY'
import json, sys
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    line = line.strip()
    if not line:
        continue
    try:
        event = json.loads(line)
    except json.JSONDecodeError:
        continue
    if not isinstance(event, dict):
        continue
    if event.get("level") == "Warning" and event.get("RejectedLogLevel") == "loud":
        print(f"    {event.get('message')}")
        sys.exit(0)
sys.exit(1)
PY
  then
    pass "a structured warning names the rejected value"
  else
    fail "no structured warning for the rejected log level"
  fi
else
  fail "server did not start with an invalid log level"
fi

echo
if [[ "${FAILED}" == 0 ]]; then echo "OBSERVABILITY: all gates passed"; else echo "OBSERVABILITY: FAILURES present"; exit 1; fi
