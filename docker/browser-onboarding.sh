#!/usr/bin/env bash
# Browser/onboarding gate for the distributable Tesserafin image
# (issue #115 / [A1.2], gating #89 / [A3]).
#
# WHY THIS EXISTS
#
#   The previous A3 gate (docker/compose-smoke.sh) asserted only that
#   `/System/Info/Public` answered. The published image ran with `--nowebclient`
#   and served the Swagger API documentation at `/`, so that gate passed while
#   the product was uninstallable in a browser. API reachability is not
#   browser-installability.
#
# WHAT THIS PROVES, against the real candidate image on pristine volumes:
#
#   * the server identity endpoint answers with the expected identity
#   * `/` resolves to HTTP 200 with an HTML content type
#     (through the 302 -> /web/ hop, which is exactly what `--nowebclient` breaks)
#   * the document served is the Tesserafin Web production bundle, NOT Swagger
#   * every script/style/manifest referenced by index.html is retrievable
#   * nothing pre-seeded onboarding before the browser ran
#   * a real browser reaches the first-run wizard, creates the admin account,
#     adds /media as a library and completes onboarding
#   * completion survives a container restart AND a container recreation
#   * /media stays read-only throughout
#
#   Plus a NEGATIVE GUARD that fails on any image built the old way, so the
#   API-only image can never satisfy this gate again.
#
# Usage: docker/browser-onboarding.sh <image-ref> [host-port]
set -euo pipefail

IMAGE="${1:?usage: docker/browser-onboarding.sh <image-ref> [host-port]}"
PORT="${2:-18196}"
BASE="http://127.0.0.1:${PORT}"
WEBDIR="/opt/tesserafin-web"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE_DIR="${REPO_ROOT}/docker/browser-gate"

WORK="$(mktemp -d)"
CNAME="tesserafin-onboarding-$$"
FAILED=0

pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }
die()  { echo "ONBOARDING-GATE: ABORT — $*" >&2; exit 1; }

cleanup() {
  docker rm -f "${CNAME}" >/dev/null 2>&1 || true
  docker run --rm -v "${WORK}:/w" busybox chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}"
}
trap cleanup EXIT

start_container() {
  docker run -d --name "${CNAME}" \
    -p "127.0.0.1:${PORT}:8096" \
    -v "${WORK}/config:/config" \
    -v "${WORK}/cache:/cache" \
    -v "${WORK}/data:/data" \
    -v "${WORK}/media:/media:ro" \
    "${IMAGE}" >/dev/null
}

# Readiness must be the WEB CLIENT, not the API. `/System/Info/Public` is
# answered by the startup SetupServer while the main application is still coming
# up, so an API-only readiness probe races: `/` still returns 503 at that point.
# Waiting for the root redirect is the only signal that the hosted web client is
# actually being served. (Getting this wrong is a smaller cousin of the very bug
# this gate exists to catch: treating an API response as proof of the UI.)
wait_for_web() {
  for _ in $(seq 1 120); do
    if [[ "$(curl -s -o /dev/null -w '%{http_code}' "${BASE}/")" == "302" ]] \
       && curl -fsS "${BASE}/System/Info/Public" >/dev/null 2>&1; then
      return 0
    fi
    if ! docker ps -q --filter "name=${CNAME}" | grep -q .; then
      echo "  container exited early; logs:"; docker logs "${CNAME}" 2>&1 | tail -40
      return 1
    fi
    sleep 2
  done
  return 1
}

# Key lookups are case-insensitive on purpose. The startup SetupServer answers
# /System/Info/Public in camelCase ("startupWizardCompleted") while the running
# server answers in PascalCase ("StartupWizardCompleted"); a case-sensitive read
# would silently yield None and could be mistaken for "not completed".
json_field() { # <file> <field> -> value, lowercased; empty if absent
  python3 - "$1" "$2" <<'PY'
import json, sys
data = json.load(open(sys.argv[1]))
want = sys.argv[2].lower()
for k, v in data.items():
    if k.lower() == want:
        print(str(v).lower()); break
PY
}

wizard_completed() { # -> "true" / "false"
  curl -fsS "${BASE}/System/Info/Public" -o "${WORK}/state.json"
  json_field "${WORK}/state.json" StartupWizardCompleted
}

echo "== negative guard: the image must actually bundle a web client =="
# This block is the whole point of the exercise: it is what the API-only image
# `12.0.0-dev.e2999e4e2feb` fails. Keep it first so a wrong image is rejected
# before any container starts.
CMD_JSON="$(docker inspect -f '{{json .Config.Cmd}}' "${IMAGE}")"
echo "  Config.Cmd: ${CMD_JSON}"
if grep -q -- '--nowebclient' <<<"${CMD_JSON}"; then
  die "the image runs with --nowebclient; it cannot serve the onboarding wizard (#115)"
fi
pass "the final command does not contain --nowebclient"

if ! grep -q -- '--webdir' <<<"${CMD_JSON}"; then
  die "the image does not pass --webdir; no web client would be hosted (#115)"
fi
pass "the final command hosts the bundled web client via --webdir"

if ! docker run --rm --entrypoint sh "${IMAGE}" -c "test -s ${WEBDIR}/index.html"; then
  die "${WEBDIR}/index.html is absent from the image (#115)"
fi
pass "${WEBDIR}/index.html is present in the image"

WEB_REV="$(docker inspect -f '{{index .Config.Labels "org.tesserafin.web.revision"}}' "${IMAGE}")"
if [[ ! "${WEB_REV}" =~ ^[0-9a-f]{40}$ ]]; then
  die "the image does not record a paired tesserafin-web commit (label org.tesserafin.web.revision = '${WEB_REV}')"
fi
REV_IN_IMAGE="$(docker run --rm --entrypoint cat "${IMAGE}" /opt/tesserafin-web.revision.json \
                | python3 -c 'import json,sys; print(json.load(sys.stdin)["revision"])')"
[[ "${REV_IN_IMAGE}" == "${WEB_REV}" ]] \
  || die "label web revision (${WEB_REV}) disagrees with the bundled manifest (${REV_IN_IMAGE})"
pass "paired tesserafin-web commit ${WEB_REV} recorded in both label and image content"

echo
echo "== pristine boot =="
mkdir -p "${WORK}/config" "${WORK}/cache" "${WORK}/data" "${WORK}/media/Movies"
echo "hello-tesserafin" > "${WORK}/media/probe.txt"
docker run --rm -v "${WORK}:/w" busybox chown -R 10000:10000 /w/config /w/cache /w/data
# Pristine means pristine: nothing but empty directories.
[[ -z "$(ls -A "${WORK}/config")" && -z "$(ls -A "${WORK}/data")" ]] \
  || die "/config or /data is not pristine before the run"
pass "/config and /data are empty before first boot"

start_container
wait_for_web || die "the web client was never served on ${BASE}"

echo
echo "== server identity =="
curl -fsS "${BASE}/System/Info/Public" -o "${WORK}/identity.json"
cat "${WORK}/identity.json"; echo
if python3 - "${WORK}/identity.json" <<'PY'
import json, sys
raw = json.load(open(sys.argv[1]))
info = {k.lower(): v for k, v in raw.items()}          # see json_field(): casing varies
assert info.get("productname") or info.get("servername"), "no server identity fields"
assert info.get("version"), "no server version"
# PascalCase means the running server answered, not the startup SetupServer.
assert "ProductName" in raw, "identity came from the startup SetupServer, not the running server"
print(f"  identity: {info.get('productname')} {info.get('version')} (Id {info.get('id')})")
PY
then
  pass "/System/Info/Public returns a server identity"
else
  fail "unexpected server identity"
fi

echo
echo "== the root path serves the web client, not the API documentation =="
ROOT_STATUS="$(curl -s -o /dev/null -w '%{http_code}' "${BASE}/")"
ROOT_REDIRECT="$(curl -s -o /dev/null -w '%{redirect_url}' "${BASE}/")"
FINAL_STATUS="$(curl -sL -o "${WORK}/root.html" -w '%{http_code}' "${BASE}/")"
FINAL_URL="$(curl -sL -o /dev/null -w '%{url_effective}' "${BASE}/")"
FINAL_CT="$(curl -sL -o /dev/null -w '%{content_type}' "${BASE}/")"
echo "  /            -> ${ROOT_STATUS} ${ROOT_REDIRECT}"
echo "  followed     -> ${FINAL_STATUS} ${FINAL_URL} (${FINAL_CT})"

# The 302 hop is recorded explicitly rather than engineered away: it is the
# server's own serving model, and it is precisely what flips to
# `/api-docs/swagger` when the web client is not hosted.
[[ "${ROOT_REDIRECT}" == *"/web/"* ]] \
  && pass "/ redirects to the web client (${ROOT_REDIRECT})" \
  || fail "/ redirects to '${ROOT_REDIRECT}', expected the web client"
[[ "${ROOT_REDIRECT}" == *"api-docs"* ]] && fail "/ redirects to the API documentation"
[[ "${FINAL_STATUS}" == "200" ]] && pass "/ resolves to HTTP 200" || fail "/ resolved to ${FINAL_STATUS}"
[[ "${FINAL_CT}" == text/html* ]] && pass "/ has an HTML content type (${FINAL_CT})" || fail "/ content type is ${FINAL_CT}"

if grep -qiE 'swagger-ui|redoc' "${WORK}/root.html"; then
  fail "/ serves API documentation"
else
  pass "/ does not serve API documentation"
fi
grep -qi 'Tesserafin' "${WORK}/root.html" \
  && pass "/ serves the Tesserafin Web production document" \
  || fail "/ does not identify Tesserafin"

# Swagger must still be reachable on its own route — the fix moves it off `/`,
# it does not remove it.
SWAGGER_STATUS="$(curl -s -o /dev/null -w '%{http_code}' -L "${BASE}/api-docs/swagger/index.html")"
echo "  /api-docs/swagger/index.html -> ${SWAGGER_STATUS}"

echo
echo "== every asset referenced by index.html is retrievable over HTTP =="
if python3 - "${WORK}/root.html" "${FINAL_URL}" <<'PY'
import re, sys, urllib.request, urllib.error
from urllib.parse import urljoin, urlparse, unquote

html = open(sys.argv[1], encoding="utf-8", errors="replace").read()
base = sys.argv[2]
refs = re.findall(r'(?:src|href)="([^"]+)"', html)
checked = broken = 0
for ref in refs:
    if ref.startswith(("data:", "#", "mailto:")) or urlparse(ref).netloc:
        continue
    url = urljoin(base, ref)
    checked += 1
    try:
        with urllib.request.urlopen(url, timeout=30) as r:
            if r.status != 200:
                print(f"    {r.status} {unquote(ref)}"); broken += 1
    except urllib.error.HTTPError as e:
        print(f"    {e.code} {unquote(ref)}"); broken += 1
    except Exception as e:                                  # noqa: BLE001
        print(f"    ERR {unquote(ref)}: {e}"); broken += 1
print(f"  {checked - broken}/{checked} referenced assets served with 200")
sys.exit(1 if broken else 0)
PY
then
  pass "all critical scripts/styles referenced by index.html are served"
else
  fail "some assets referenced by index.html are not retrievable"
fi

echo
echo "== no pre-seeding: onboarding must still be incomplete =="
COMPLETED="$(wizard_completed)"
echo "  StartupWizardCompleted = ${COMPLETED}"
[[ "${COMPLETED}" == "false" ]] \
  && pass "onboarding is NOT marked complete before the browser test" \
  || die "onboarding was already complete before the browser ran — the gate would be vacuous"

echo
echo "== read-only media =="
docker exec "${CNAME}" cat /media/probe.txt >/dev/null 2>&1 \
  && pass "read-only media mount is readable" || fail "could not read /media/probe.txt"
if docker exec "${CNAME}" sh -c 'touch /media/.should-fail' 2>/dev/null; then
  fail "read-only media mount is writable (should be ro)"
else
  pass "read-only media mount rejects writes"
fi

echo
echo "== real browser onboarding (Playwright) =="
command -v npm >/dev/null || die "npm is required to run the browser gate"
( cd "${GATE_DIR}" && npm ci --no-audit --no-fund >/dev/null )
( cd "${GATE_DIR}" && npx --no-install playwright install chromium >/dev/null 2>&1 || \
  npx --no-install playwright install chromium )
if ( cd "${GATE_DIR}" && TESSERAFIN_BASE_URL="${BASE}" npx --no-install playwright test ); then
  pass "browser completed the first-run wizard end to end"
else
  fail "browser onboarding failed"
fi

echo
echo "== onboarding survives a container restart =="
docker restart -t 30 "${CNAME}" >/dev/null
wait_for_web || fail "the container did not come back after restart"
AFTER_RESTART="$(wizard_completed)"
echo "  StartupWizardCompleted after restart = ${AFTER_RESTART}"
[[ "${AFTER_RESTART}" == "true" ]] \
  && pass "completed onboarding survives a restart" \
  || fail "onboarding state lost across restart"

echo
echo "== onboarding survives container recreation on the same volumes =="
docker rm -f "${CNAME}" >/dev/null
start_container
wait_for_web || fail "the recreated container did not come up"
AFTER_RECREATE="$(wizard_completed)"
echo "  StartupWizardCompleted after recreation = ${AFTER_RECREATE}"
[[ "${AFTER_RECREATE}" == "true" ]] \
  && pass "completed onboarding survives container recreation" \
  || fail "onboarding state lost across container recreation"

if docker exec "${CNAME}" sh -c 'touch /media/.should-fail-2' 2>/dev/null; then
  fail "read-only media mount became writable after recreation"
else
  pass "read-only media mount still rejects writes after recreation"
fi

echo
if [[ "${FAILED}" == 0 ]]; then
  echo "ONBOARDING-GATE: all gates passed"
else
  echo "ONBOARDING-GATE: FAILURES present"
  exit 1
fi
