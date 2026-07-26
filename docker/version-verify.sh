#!/usr/bin/env bash
# shellcheck disable=SC2015,SC2317,SC2329
# SC2015: `<cond> && pass || fail` is intended — pass()/fail() both return 0.
# SC2317/SC2329: cleanup() is invoked indirectly via the EXIT trap.
#
# Proves that every version surface of a produced image agrees (#92 / [A6]).
#
# Unit tests can only check tag derivation from SharedVersion.cs. They cannot see
# what a built image actually carries, so this script closes the other half:
#
#   image tag          <->  SharedVersion.cs
#   OCI image.version  <->  SharedVersion.cs
#   OCI image.revision <->  the exact source commit (and its 12-char prefix is
#                           the one embedded in a dev tag)
#   /health version    <->  OCI image.version   (running application, real HTTP)
#   /System/Info/Public version <-> /health version
#
# Usage:
#   docker/version-verify.sh <image-ref> [host-port] [--expect-commit <sha>]
#                            [--expect-version <x.y.z>] [--require-digest]
#
#   --require-digest   fail unless the reference resolves to a registry digest.
#                      Evidence reports must use this; a freshly built local
#                      image has no digest until it is pushed.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONTRACT="${REPO_ROOT}/docker/version-contract.sh"

IMAGE=""
PORT="19296"
EXPECT_COMMIT=""
EXPECT_VERSION=""
REQUIRE_DIGEST=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --expect-commit)  EXPECT_COMMIT="$2"; shift 2 ;;
    --expect-version) EXPECT_VERSION="$2"; shift 2 ;;
    --require-digest) REQUIRE_DIGEST=1; shift ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *)
      if [[ -z "${IMAGE}" ]]; then IMAGE="$1"; else PORT="$1"; fi
      shift ;;
  esac
done

[[ -n "${IMAGE}" ]] || { echo "usage: docker/version-verify.sh <image-ref> [port] [options]" >&2; exit 2; }

FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }

CONTAINER="tf-verver-$$"
cleanup() { docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true; }
trap cleanup EXIT

# Case-insensitive JSON field read: the server has shipped both `Version` and
# `version` spellings across endpoints, and a case-sensitive probe silently
# reports "missing" instead of "mismatch".
json_field() { # $1 = field name (case-insensitive)
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

echo "== image under test: ${IMAGE} =="

# --- 0. canonical expectations ----------------------------------------------
SRC_VERSION="${EXPECT_VERSION:-$("${CONTRACT}" version)}"
echo "  canonical version (SharedVersion.cs) : ${SRC_VERSION}"

# --- 1. resolve to an immutable digest --------------------------------------
docker image inspect "${IMAGE}" >/dev/null 2>&1 || docker pull -q "${IMAGE}" >/dev/null
DIGEST="$(docker image inspect --format '{{if .RepoDigests}}{{index .RepoDigests 0}}{{end}}' "${IMAGE}")"
if [[ -n "${DIGEST}" ]]; then
  pass "resolves to an immutable digest: ${DIGEST}"
elif [[ "${REQUIRE_DIGEST}" == "1" ]]; then
  fail "no registry digest for ${IMAGE} — push it before using it as pinned evidence"
else
  echo "  NOTE  no registry digest yet (local build); image id $(docker image inspect --format '{{.Id}}' "${IMAGE}")"
fi

ARCH="$(docker image inspect --format '{{.Architecture}}' "${IMAGE}")"
echo "  architecture : ${ARCH}"

# --- 2. tag <-> source version ----------------------------------------------
# Only a version-bearing tag can be checked; a digest reference or `sha-<commit>`
# carries no version, which is correct and not a failure.
TAG="${IMAGE##*:}"
if [[ "${IMAGE}" == *"@sha256:"* ]]; then
  echo "  NOTE  reference is a digest — no tag to compare (the OCI label check below covers it)"
elif [[ "${TAG}" == sha-* ]]; then
  echo "  NOTE  reference is a sha- tag — carries a commit, not a version"
else
  TAG_CORE="${TAG%%-*}"
  [[ "${TAG_CORE}" == "${SRC_VERSION}" ]] \
    && pass "tag version '${TAG_CORE}' == SharedVersion.cs '${SRC_VERSION}'" \
    || fail "tag version '${TAG_CORE}' != SharedVersion.cs '${SRC_VERSION}'"
fi

# --- 3. OCI labels ------------------------------------------------------------
LBL_VERSION="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}}' "${IMAGE}")"
LBL_REVISION="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "${IMAGE}")"

[[ "${LBL_VERSION}" == "${SRC_VERSION}" ]] \
  && pass "org.opencontainers.image.version '${LBL_VERSION}' == SharedVersion.cs" \
  || fail "org.opencontainers.image.version '${LBL_VERSION}' != SharedVersion.cs '${SRC_VERSION}'"

[[ "${LBL_REVISION}" =~ ^[0-9a-f]{40}$ ]] \
  && pass "org.opencontainers.image.revision is a full commit sha (${LBL_REVISION})" \
  || fail "org.opencontainers.image.revision '${LBL_REVISION}' is not a 40-char commit sha"

if [[ -n "${EXPECT_COMMIT}" ]]; then
  [[ "${LBL_REVISION}" == "${EXPECT_COMMIT}" ]] \
    && pass "revision == expected source commit" \
    || fail "revision '${LBL_REVISION}' != expected source commit '${EXPECT_COMMIT}'"
fi

# A dev tag embeds the first 12 characters of the revision. If they disagree the
# tag names one commit and the image was built from another.
if [[ "${TAG}" == *-dev.* ]]; then
  TAG_SHORT="${TAG##*-dev.}"
  [[ "${LBL_REVISION:0:12}" == "${TAG_SHORT}" ]] \
    && pass "dev tag commit prefix '${TAG_SHORT}' == revision prefix" \
    || fail "dev tag commit prefix '${TAG_SHORT}' != revision prefix '${LBL_REVISION:0:12}'"
fi

# --- 4. running application ---------------------------------------------------
# /health answers from the startup server long before the real pipeline is up,
# with 503 + status=starting on the same JSON schema. Readiness is 200 AND
# status=healthy — nothing weaker.
docker run -d --name "${CONTAINER}" -p "127.0.0.1:${PORT}:8096" "${IMAGE}" >/dev/null
READY=0
for _ in $(seq 1 150); do   # up to ~300s
  BODY="$(curl -fsS "http://127.0.0.1:${PORT}/health" 2>/dev/null || true)"
  if [[ -n "${BODY}" ]] && [[ "$(json_field status <<<"${BODY}")" == "healthy" ]]; then
    READY=1; break
  fi
  sleep 2
done
if [[ "${READY}" != "1" ]]; then
  fail "container never reached /health 200 status=healthy"
  docker logs "${CONTAINER}" 2>&1 | tail -30
  echo; echo "VERSION-VERIFY: FAILED"; exit 1
fi
pass "/health reached 200 status=healthy"

HEALTH_VERSION="$(json_field version <<<"${BODY}")"
[[ "${HEALTH_VERSION}" == "${LBL_VERSION}" ]] \
  && pass "/health version '${HEALTH_VERSION}' == org.opencontainers.image.version" \
  || fail "/health version '${HEALTH_VERSION}' != OCI label '${LBL_VERSION}'"
[[ "${HEALTH_VERSION}" == "${SRC_VERSION}" ]] \
  && pass "/health version == SharedVersion.cs" \
  || fail "/health version '${HEALTH_VERSION}' != SharedVersion.cs '${SRC_VERSION}'"

INFO="$(curl -fsS "http://127.0.0.1:${PORT}/System/Info/Public" 2>/dev/null || true)"
INFO_VERSION="$(json_field version <<<"${INFO}")"
[[ -n "${INFO_VERSION}" && "${INFO_VERSION}" == "${HEALTH_VERSION}" ]] \
  && pass "application-reported version '${INFO_VERSION}' == /health version" \
  || fail "application-reported version '${INFO_VERSION}' != /health version '${HEALTH_VERSION}'"

echo
if [[ "${FAILED}" == 0 ]]; then
  echo "VERSION-VERIFY: image tag, SharedVersion.cs, OCI labels, /health and the running application all agree"
  exit 0
else
  echo "VERSION-VERIFY: FAILED"
  exit 1
fi
