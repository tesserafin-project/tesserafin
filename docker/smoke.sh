#!/usr/bin/env bash
# Runtime smoke test for the Tesserafin production image (#87 / [A1]).
#
# Proves the container-level gates from a clean host with NO source checkout
# mounted: non-root, API answers, pinned ffmpeg, writable runtime dirs,
# read-only media stays readable, empty first-run boot, graceful SIGTERM, and
# a restart on the same state.
#
# Usage: docker/smoke.sh <image-ref> [host-port]
set -euo pipefail

IMAGE="${1:?usage: smoke.sh <image-ref> [host-port]}"
PORT="${2:-18096}"
FFMPEG_EXPECT="7.1.4"
# Where the pinned Tesserafin Web production bundle lives in the image (#115).
WEBDIR="/opt/tesserafin-web"
# Graceful-shutdown budget. 30s suits native/real hardware; raise it for
# emulated runs (arm64 under QEMU is ~10-50x slower), e.g. STOP_TIMEOUT=120.
STOP_TIMEOUT="${STOP_TIMEOUT:-30}"

WORK="$(mktemp -d)"
CNAME="tesserafin-smoke-$$"
mkdir -p "${WORK}/config" "${WORK}/cache" "${WORK}/data" "${WORK}/media"
echo "hello-tesserafin" > "${WORK}/media/probe.txt"
# Runtime volumes must be owned by the container's non-root uid (10000). This is
# the operator's volume-prep step (full policy is #88); do it via a root helper
# container so no host sudo is needed. Media stays owned by the host (read-only).
docker run --rm -v "${WORK}:/w" busybox chown -R 10000:10000 /w/config /w/cache /w/data

pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }
FAILED=0

cleanup() {
  docker rm -f "${CNAME}" >/dev/null 2>&1 || true
  # Files under the volumes were created by uid 10000; hand them back so the
  # host user can remove the scratch tree.
  docker run --rm -v "${WORK}:/w" busybox chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}"
}
trap cleanup EXIT

echo "== image inspection =="
ENTRYPOINT_JSON="$(docker inspect -f '{{json .Config.Entrypoint}}' "${IMAGE}")"
USER_CFG="$(docker inspect -f '{{.Config.User}}' "${IMAGE}")"
echo "  entrypoint: ${ENTRYPOINT_JSON}"
echo "  config.User: ${USER_CFG}"
[[ "${USER_CFG}" == "10000:10000" ]] && pass "image declares non-root user" || fail "image user is '${USER_CFG}', expected 10000:10000"
docker inspect -f '{{range $k,$v := .Config.Labels}}  {{$k}}={{$v}}
{{end}}' "${IMAGE}" | grep -q 'org.opencontainers.image.source' && pass "OCI source label present" || fail "OCI source label missing"
# No SDK / compiler baked in. The .NET *runtime* muxer (/usr/bin/dotnet) is
# expected in an aspnet image; what must be absent is the SDK, MSBuild and the
# C# compiler.
if docker run --rm --entrypoint sh "${IMAGE}" -c '
      set -e
      sdks="$(dotnet --list-sdks 2>/dev/null || true)"
      [ -z "$sdks" ] || { echo "sdks: $sdks"; exit 1; }
      ! ls -d /usr/share/dotnet/sdk >/dev/null 2>&1
      comp="$(find / -xdev \( -name csc.dll -o -name MSBuild.dll \) 2>/dev/null | head -1)"
      [ -z "$comp" ] || { echo "compiler: $comp"; exit 1; }
   '; then
  pass "no .NET SDK / compiler / MSBuild in runtime image"
else
  fail "SDK or compiler present in runtime image"
fi
if docker run --rm --entrypoint sh "${IMAGE}" -c 'ls /opt/tesserafin/*.csproj /src /repo 2>/dev/null | grep -q .'; then
  fail "source checkout present in runtime image"
else
  pass "no source checkout in runtime image"
fi

# A build input, not a runtime: the bundled web client must not drag Node.js in.
if docker run --rm --entrypoint sh "${IMAGE}" -c 'command -v node >/dev/null 2>&1 || command -v npm >/dev/null 2>&1'; then
  fail "a Node.js runtime is present in the image"
else
  pass "no Node.js runtime in the image"
fi

echo "== bundled web client (#115 / [A1.2]) =="
# REGRESSION GUARD. The A1/A1.1 image ran with `--nowebclient` and shipped no web
# bundle, so `/` redirected to `/api-docs/swagger` and no onboarding wizard
# existed — while the smoke of the day passed, because it only polled the API.
# These four assertions fail on any image built that way.
CMD_JSON="$(docker inspect -f '{{json .Config.Cmd}}' "${IMAGE}")"
echo "  config.Cmd: ${CMD_JSON}"
if grep -q -- '--nowebclient' <<<"${CMD_JSON}"; then
  fail "the final command contains --nowebclient; the image cannot serve the onboarding wizard"
else
  pass "the final command does not contain --nowebclient"
fi
if grep -q -- "--webdir" <<<"${CMD_JSON}"; then
  pass "the final command hosts the bundled web client via --webdir"
else
  fail "the final command does not pass --webdir"
fi
if docker run --rm --entrypoint sh "${IMAGE}" -c "test -s ${WEBDIR}/index.html"; then
  pass "${WEBDIR}/index.html is bundled"
else
  fail "${WEBDIR}/index.html is absent from the image"
fi
# The paired tesserafin-web commit must be recorded in BOTH the OCI label and the
# image content, and the two must agree — otherwise the pairing recorded in the
# release notes could silently drift from what actually ships.
WEB_REV_LABEL="$(docker inspect -f '{{index .Config.Labels "org.tesserafin.web.revision"}}' "${IMAGE}")"
WEB_REV_FILE="$(docker run --rm --entrypoint cat "${IMAGE}" /opt/tesserafin-web.revision.json 2>/dev/null \
                 | python3 -c 'import json,sys; print(json.load(sys.stdin)["revision"])' 2>/dev/null || true)"
echo "  paired web revision: label=${WEB_REV_LABEL} image=${WEB_REV_FILE}"
if [[ "${WEB_REV_LABEL}" =~ ^[0-9a-f]{40}$ && "${WEB_REV_LABEL}" == "${WEB_REV_FILE}" ]]; then
  pass "paired tesserafin-web commit recorded consistently in label and image content"
else
  fail "paired tesserafin-web commit missing or inconsistent (label='${WEB_REV_LABEL}' image='${WEB_REV_FILE}')"
fi

echo "== boot from empty first-run state =="
docker run -d --name "${CNAME}" \
  -p "127.0.0.1:${PORT}:8096" \
  -v "${WORK}/config:/config" \
  -v "${WORK}/cache:/cache" \
  -v "${WORK}/data:/data" \
  -v "${WORK}/media:/media:ro" \
  "${IMAGE}" >/dev/null

# Wait for the API on its documented port (host-side curl; none needed in image).
API_OK=0
for _ in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:${PORT}/System/Info/Public" >/dev/null 2>&1; then API_OK=1; break; fi
  if ! docker ps -q --filter "name=${CNAME}" | grep -q .; then
    echo "  container exited early; logs:"; docker logs "${CNAME}" 2>&1 | tail -30; break
  fi
  sleep 2
done
[[ "${API_OK}" == 1 ]] && pass "API answers on :8096 (/System/Info/Public)" || fail "API did not answer within 120s"
if [[ "${API_OK}" == 1 ]]; then
  # Captured then truncated, NOT `curl ... | head -c 300`. `head` closes the pipe
  # after 300 bytes, curl reports "Recv failure" (56), and under `set -euo pipefail`
  # that aborts the whole smoke run — on a purely cosmetic line.
  INFO_BODY="$(curl -fsS "http://127.0.0.1:${PORT}/System/Info/Public")"
  printf '%s\n' "${INFO_BODY:0:300}"

  # READINESS, not a race. `/System/Info/Public` is answered by the startup
  # SetupServer while the main application is still coming up, so the API
  # responding does NOT mean the web client is being served yet — polling only
  # the API and then probing `/` yields a spurious 503. Wait for the root path
  # to actually redirect before asserting anything about it.
  for _ in $(seq 1 60); do
    [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${PORT}/")" == "302" ]] && break
    sleep 2
  done

  # `/` must reach the web client. It is a 302 to `/web/` (the server's own
  # serving model — BaseUrlRedirectionMiddleware + static files mounted at
  # RequestPath "/web"), which is exactly what flips to `/api-docs/swagger` when
  # the web client is not hosted. Follow the hop and check where it lands.
  ROOT_REDIRECT="$(curl -s -o /dev/null -w '%{redirect_url}' "http://127.0.0.1:${PORT}/")"
  ROOT_STATUS="$(curl -sL -o "${WORK}/root.html" -w '%{http_code}' "http://127.0.0.1:${PORT}/")"
  ROOT_CT="$(curl -sL -o /dev/null -w '%{content_type}' "http://127.0.0.1:${PORT}/")"
  echo "  / -> ${ROOT_REDIRECT} -> ${ROOT_STATUS} (${ROOT_CT})"
  if [[ "${ROOT_REDIRECT}" == *"/web/"* && "${ROOT_STATUS}" == "200" && "${ROOT_CT}" == text/html* ]]; then
    pass "/ serves the web client as HTML (302 -> /web/, 200)"
  else
    fail "/ did not serve the web client (redirect='${ROOT_REDIRECT}' status=${ROOT_STATUS} type=${ROOT_CT})"
  fi
  if grep -qiE 'swagger-ui|redoc' "${WORK}/root.html"; then
    fail "/ serves API documentation instead of the web client"
  else
    pass "/ does not serve API documentation"
  fi
fi

echo "== health endpoint (#91 / [A5]) =="
# Minimal guard only. The full A5 gate — schema, version agreement, JSON-lines
# logs, log-level switching — lives in docker/observability.sh.
HEALTH_CODE="$(curl -s -o "${WORK}/health.json" -w '%{http_code}' "http://127.0.0.1:${PORT}/health")"
echo "  /health -> ${HEALTH_CODE}: $(head -c 200 "${WORK}/health.json")"
if [[ "${HEALTH_CODE}" == "200" ]] \
   && python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); sys.exit(0 if d.get("status")=="healthy" and d.get("database")=="healthy" and d.get("version") else 1)' "${WORK}/health.json"; then
  pass "/health returns 200 with a healthy database and a version"
else
  fail "/health did not return the healthy contract (status ${HEALTH_CODE})"
fi

echo "== runtime identity & tooling (inside container) =="
UID_IN="$(docker exec "${CNAME}" id -u)"
[[ "${UID_IN}" == "10000" ]] && pass "runs as non-root uid 10000" || fail "uid is ${UID_IN}"

FFOUT="$(docker exec "${CNAME}" /usr/lib/jellyfin-ffmpeg/ffmpeg -version 2>&1 | head -1 || true)"
echo "  ${FFOUT}"
echo "${FFOUT}" | grep -q "${FFMPEG_EXPECT}" && pass "ffmpeg reports pinned ${FFMPEG_EXPECT}" || fail "ffmpeg version mismatch (expected ${FFMPEG_EXPECT})"

for d in /config /cache /data; do
  if docker exec "${CNAME}" sh -c "touch ${d}/.smoke-write && rm -f ${d}/.smoke-write"; then
    pass "writable: ${d}"
  else
    fail "not writable: ${d}"
  fi
done

READ_OK="$(docker exec "${CNAME}" cat /media/probe.txt 2>/dev/null || true)"
[[ "${READ_OK}" == "hello-tesserafin" ]] && pass "read-only media mount is readable" || fail "could not read /media/probe.txt"
if docker exec "${CNAME}" sh -c 'touch /media/.should-fail' 2>/dev/null; then
  fail "read-only media mount is writable (should be ro)"
else
  pass "read-only media mount rejects writes"
fi

echo "== graceful SIGTERM (budget ${STOP_TIMEOUT}s) =="
START="$(date +%s)"
docker stop -t "${STOP_TIMEOUT}" "${CNAME}" >/dev/null
ELAPSED=$(( $(date +%s) - START ))
EXITCODE="$(docker inspect -f '{{.State.ExitCode}}' "${CNAME}")"
echo "  stop took ${ELAPSED}s, exit code ${EXITCODE}"
echo "  shutdown log tail:"; docker logs "${CNAME}" 2>&1 | grep -iE 'shutting down|stopping|shutdown|SIGTERM|graceful' | tail -3 | sed 's/^/    /'
{ [[ "${EXITCODE}" == "0" || "${EXITCODE}" == "143" ]] && [[ "${ELAPSED}" -lt "${STOP_TIMEOUT}" ]]; } \
  && pass "SIGTERM handled gracefully (exit ${EXITCODE}, no forced kill)" || fail "did not shut down cleanly on SIGTERM (exit ${EXITCODE} after ${ELAPSED}s)"

echo "== restart on the same runtime state =="
docker start "${CNAME}" >/dev/null
RES_OK=0
for _ in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:${PORT}/System/Info/Public" >/dev/null 2>&1; then RES_OK=1; break; fi
  sleep 2
done
[[ "${RES_OK}" == 1 ]] && pass "restart with existing state boots and answers" || fail "restart did not come back up"

echo
if [[ "${FAILED}" == 0 ]]; then echo "SMOKE: all gates passed"; else echo "SMOKE: FAILURES present"; exit 1; fi
