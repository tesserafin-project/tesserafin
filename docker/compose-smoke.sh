#!/usr/bin/env bash
# shellcheck disable=SC2015,SC2317
# SC2015: `<cond> && pass || fail` is intended — pass()/fail() both return 0.
# SC2317: cleanup() runs via the EXIT trap.
# Bounded compose smoke for the guided install (#89 / [A3]).
#
# Runs the canonical docker-compose.yml from an ISOLATED directory that contains
# ONLY the compose file + env example (no repo source), proving the zero-friction
# path: pull the immutable image, boot, reach the API, survive a restart, keep
# state across container recreation, enforce read-only media, and keep named
# volumes on `down`. Requires NO source compilation.
#
# Usage: docker/compose-smoke.sh [host-port]
set -euo pipefail

PORT="${1:-8096}"
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
PROJECT="tf_a3_$$"
API="http://127.0.0.1:${PORT}/System/Info/Public"

FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }
dc() { docker compose -p "${PROJECT}" "$@"; }

cleanup() {
  ( cd "${WORK}" && dc down -v >/dev/null 2>&1 ) || true
  docker run --rm -v "${WORK}:/w" busybox chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}"
}
trap cleanup EXIT

wait_api() { for _ in $(seq 1 90); do curl -fsS "${API}" >/dev/null 2>&1 && return 0; sleep 2; done; return 1; }

echo "== isolated workdir (only compose + env + empty media; NO source) =="
cp "${SRC_DIR}/docker-compose.yml" "${WORK}/docker-compose.yml"
cp "${SRC_DIR}/.env.example" "${WORK}/.env.example"
mkdir -p "${WORK}/media"
echo "hello-tesserafin" > "${WORK}/media/probe.txt"
# Map the requested host port without editing the tracked compose file.
printf 'TESSERAFIN_MEDIA=%s/media\n' "${WORK}" > "${WORK}/.env"
[[ "${PORT}" != "8096" ]] && cat > "${WORK}/docker-compose.override.yml" <<YAML
services:
  tesserafin:
    ports:
      - "${PORT}:8096"
YAML
ls "${WORK}"
[[ ! -e "${WORK}/Dockerfile" && ! -e "${WORK}/src" ]] && pass "no source / Dockerfile in the install dir" || fail "source present in install dir"

cd "${WORK}"

echo "== docker compose config (renders + validates) =="
dc config >/dev/null && pass "compose config valid" || fail "compose config invalid"
IMG_REF="$(dc config | awk '/image:/{print $2; exit}')"
echo "  image: ${IMG_REF}"
case "${IMG_REF}" in *@sha256:*|*:12.0.0-dev.*) pass "compose uses an immutable pre-release tag" ;; *) fail "compose image is not the immutable dev tag: ${IMG_REF}" ;; esac

echo "== docker compose pull (from GHCR, no build) =="
dc pull >/dev/null 2>&1 && pass "image pulled via compose" || fail "compose pull failed"

echo "== fresh named-volume install: up + API reachable + first-run state =="
dc up -d >/dev/null
wait_api || { fail "API never answered"; dc logs --tail 40; exit 1; }
pass "server reachable on :${PORT} after compose up"
dc exec -T tesserafin id -u | grep -qx 10000 && pass "runs as non-root uid 10000" || fail "not uid 10000"
# Media is read-only.
if dc exec -T tesserafin sh -c 'touch /media/.should-fail' 2>/dev/null; then fail "media is writable (should be ro)"; else pass "media mounted read-only (write rejected)"; fi
dc exec -T tesserafin cat /media/probe.txt 2>/dev/null | grep -qx hello-tesserafin && pass "read-only media is readable" || fail "media not readable"
# A DB was created on the data volume (first-run state).
dc exec -T tesserafin sh -c 'ls /data/data/*.db >/dev/null 2>&1' && pass "first-run state created (/data/*.db)" || fail "no DB created on first run"

echo "== survives an in-place restart =="
dc restart >/dev/null
wait_api && pass "API reachable after restart" || fail "API gone after restart"

echo "== state persists across container RECREATION (down without -v, then up) =="
MARK="a3-persist-$$"
dc exec -T tesserafin sh -c "printf %s '${MARK}' > /config/.a3-marker"
dc down >/dev/null                      # removes container, KEEPS named volumes
# Assert volumes still exist after `down` (no -v).
docker volume ls --format '{{.Name}}' | grep -q "${PROJECT}_tesserafin_config" \
  && pass "named volumes retained after 'compose down'" || fail "named volumes destroyed by 'compose down'"
dc up -d >/dev/null
wait_api || { fail "API never answered after recreation"; dc logs --tail 40; exit 1; }
GOT="$(dc exec -T tesserafin cat /config/.a3-marker 2>/dev/null || true)"
[[ "${GOT}" == "${MARK}" ]] && pass "persistent state survived container recreation" || fail "state lost across recreation (got '${GOT}')"

echo "== down -v removes named volumes only when explicitly requested =="
dc down -v >/dev/null
docker volume ls --format '{{.Name}}' | grep -q "${PROJECT}_tesserafin_config" \
  && fail "named volumes survived 'down -v'" || pass "'compose down -v' removed named volumes"

echo
if [[ "${FAILED}" == 0 ]]; then echo "COMPOSE-SMOKE: all gates passed"; exit 0; else echo "COMPOSE-SMOKE: FAILED"; exit 1; fi
