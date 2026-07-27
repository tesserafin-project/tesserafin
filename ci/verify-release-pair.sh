#!/usr/bin/env bash
# shellcheck disable=SC2015,SC2317,SC2329
# SC2015: `<cond> && pass || fail` is intended — pass()/fail() both return 0, so
#         the `||` branch is reached only when <cond> itself was false. Same
#         convention as docker/version-verify.sh.
# SC2317/SC2329: cleanup() and the layer helpers are reached through traps.
#
# The server<->web release-pair gate (#93 / [A7]).
#
# WHAT THIS IS
#
#   ONE deterministic entry point that proves a specific server IMAGE and a
#   specific tesserafin-web COMMIT are the same release. Everything it asserts is
#   named by an immutable identity supplied on the command line: a manifest
#   digest and two 40-character commit SHAs. Nothing is read from "whatever
#   branch happens to be checked out" — the script verifies that each checkout it
#   is pointed at is actually AT the commit it was told, and refuses to run
#   otherwise.
#
#   It does not re-implement the A5/A6 gates. `docker/version-verify.sh` already
#   proves image tag = SharedVersion.cs = application version = /health version =
#   org.opencontainers.image.version with a matching revision, and
#   `docker/browser-onboarding.sh` already proves the bundled web client is
#   browser-reachable and installable. Both are INVOKED here; their assertions
#   are not copied.
#
# WHAT IT ADDS on top of those
#
#   * the image's bundled-web provenance is compared to a REAL tesserafin-web
#     checkout at a named commit — not merely to itself;
#   * the canonical OpenAPI contract is REGENERATED from the server source and
#     compared to the committed document;
#   * the tesserafin-web SDK is regenerated and proven free of drift, and its
#     pinned contract is proven byte-identical to the contract the release server
#     actually serves;
#   * the contract-critical playback lifecycle (#43/#70/#71) is driven through a
#     REAL browser against the release image, not a source-built dev server.
#
# FAIL-CLOSED. Every assertion defaults to running. There is no flag that makes a
# missing proof look like a pass; the only opt-outs are --skip-e2e and
# --skip-openapi-regen, which both mark the run DEGRADED, print what was not
# proven and force a non-zero exit.
#
# USAGE
#
#   ci/verify-release-pair.sh \
#       --server-image ghcr.io/tesserafin-project/tesserafin-server@sha256:<64 hex> \
#       --server-source <40-char commit> \
#       --web-repo /path/to/tesserafin-web \
#       --web-source <40-char commit> \
#       [--server-repo PATH]     server checkout to verify against
#                                (default: this script's repository root)
#       [--port N]               host port for the E2E container (default: auto)
#       [--lifecycle-runs N]     consecutive oracle runs, each from a freshly
#                                created container and freshly created volumes
#                                (default: 1; the A7 evidence run used 3)
#       [--e2e-spec PATH]        repeatable; overrides the default spec list
#       [--keep]                 DEBUG PRESERVATION: keep the containers, the
#                                volumes, the media fixtures and the Playwright
#                                traces of a failing run instead of removing
#                                them, and print where they are. Without it,
#                                cleanup runs on success AND on failure.
#       [--skip-e2e]             DEGRADED, see above
#       [--skip-openapi-regen]   DEGRADED, see above
#
# FAILURE OUTPUT names the layer that disagreed, so a red run says which of the
# six contracts broke rather than "the release pair is bad":
#
#   IMAGE PROVENANCE | BUNDLED WEB PROVENANCE | OPENAPI | GENERATED SDK
#   | BROWSER E2E | LIFECYCLE CONTRACT
#
# NOT hosted CI. This runs on a laptop. #97 / [C4] is the hosted, enforced gate
# and stays open until #94 / [C1] restores off-laptop enforcement.
set -euo pipefail

SERVER_IMAGE=""
SERVER_SOURCE=""
WEB_REPO=""
WEB_SOURCE=""
SERVER_REPO=""
PORT=""
LIFECYCLE_RUNS=1
KEEP=0
SKIP_E2E=0
SKIP_OPENAPI_REGEN=0
E2E_SPECS=()

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server-image)       SERVER_IMAGE="$2"; shift 2 ;;
    --server-source)      SERVER_SOURCE="$2"; shift 2 ;;
    --server-repo)        SERVER_REPO="$2"; shift 2 ;;
    --web-repo)           WEB_REPO="$2"; shift 2 ;;
    --web-source)         WEB_SOURCE="$2"; shift 2 ;;
    --port)               PORT="$2"; shift 2 ;;
    --lifecycle-runs)     LIFECYCLE_RUNS="$2"; shift 2 ;;
    --e2e-spec)           E2E_SPECS+=("$2"); shift 2 ;;
    --keep)               KEEP=1; shift ;;
    --skip-e2e)           SKIP_E2E=1; shift ;;
    --skip-openapi-regen) SKIP_OPENAPI_REGEN=1; shift ;;
    -h|--help)            sed -n '8,73p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

: "${SERVER_REPO:=${REPO_ROOT}}"

if [[ ${#E2E_SPECS[@]} -eq 0 ]]; then
  # The contract-critical set, and only it. A7 is not the B1 release-candidate
  # gate: library parity, error-message UX, responsive/a11y and bundle budget
  # are Section B and are deliberately absent.
  E2E_SPECS=(
    tests/e2e/playback-v2-lifecycle-oracle.spec.ts
    tests/e2e/playback-attempt-id-contract.spec.ts
    tests/e2e/playback-v2-server-contract.spec.ts
  )
fi

# ---------------------------------------------------------------------------
# Reporting. Every failure carries the layer that produced it.
# ---------------------------------------------------------------------------
FAILED=0
LAYER="ARGUMENTS"
declare -a FAILURES=()

layer() { LAYER="$1"; echo; echo "== ${LAYER} =="; }
pass()  { echo "  PASS  $*"; }
fail()  { echo "  FAIL  [${LAYER}] $*"; FAILURES+=("${LAYER}: $*"); FAILED=1; }
die()   { echo "RELEASE-PAIR: ABORT [${LAYER}] — $*" >&2; exit 1; }
note()  { echo "  NOTE  $*"; }

WORK=""
declare -a CONTAINERS=()

cleanup() {
  local status=$?
  for c in ${CONTAINERS[@]+"${CONTAINERS[@]}"}; do
    if [[ "${KEEP}" -eq 1 && "${status}" -ne 0 ]]; then
      echo "  KEPT container ${c} (--keep)"
    else
      docker rm -f "${c}" >/dev/null 2>&1 || true
    fi
  done
  if [[ -n "${WORK}" && -d "${WORK}" ]]; then
    if [[ "${KEEP}" -eq 1 && "${status}" -ne 0 ]]; then
      echo "  KEPT work tree ${WORK} (--keep) — media fixtures, server logs, Playwright traces"
    else
      # The container writes /config and /data as uid 10000; hand them back
      # before rm, exactly as docker/browser-onboarding.sh does.
      docker run --rm -v "${WORK}:/w" busybox chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
      rm -rf "${WORK}"
    fi
  fi
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# LAYER 0 — arguments. Immutable identities only.
# ---------------------------------------------------------------------------
layer "ARGUMENTS"

is_sha40() { [[ "$1" =~ ^[0-9a-f]{40}$ ]]; }

[[ -n "${SERVER_IMAGE}" ]] || die "--server-image is required"
[[ -n "${SERVER_SOURCE}" ]] || die "--server-source is required"
[[ -n "${WEB_REPO}" ]] || die "--web-repo is required"
[[ -n "${WEB_SOURCE}" ]] || die "--web-source is required"
[[ "${LIFECYCLE_RUNS}" =~ ^[1-9][0-9]*$ ]] || die "--lifecycle-runs must be a positive integer"

# (1) full 40-character commit identities — an abbreviation is ambiguous and a
# tag or branch name is mutable.
is_sha40 "${SERVER_SOURCE}" || die "--server-source '${SERVER_SOURCE}' is not a full 40-character commit sha"
is_sha40 "${WEB_SOURCE}"    || die "--web-source '${WEB_SOURCE}' is not a full 40-character commit sha"
pass "server and web sources are full 40-character commit identities"

# (2) the image is named by a manifest digest. A tag — even an immutable one by
# policy — is a name that a registry can be made to resolve elsewhere.
[[ "${SERVER_IMAGE}" == *"@sha256:"* ]] \
  || die "--server-image must be a manifest digest reference (…@sha256:…), got '${SERVER_IMAGE}'"
[[ "${SERVER_IMAGE##*@sha256:}" =~ ^[0-9a-f]{64}$ ]] \
  || die "--server-image digest is malformed: '${SERVER_IMAGE##*@}'"
pass "the server image is referenced by manifest digest"

WEB_REPO="$(cd "${WEB_REPO}" 2>/dev/null && pwd)" || die "--web-repo does not exist"
# `.git` is a directory in a normal clone and a file in a `git worktree`; both
# are legitimate checkouts to point this gate at.
[[ -e "${WEB_REPO}/.git" ]] || die "--web-repo is not a git checkout: ${WEB_REPO}"
SERVER_REPO="$(cd "${SERVER_REPO}" 2>/dev/null && pwd)" || die "--server-repo does not exist"
[[ -e "${SERVER_REPO}/.git" ]] || die "--server-repo is not a git checkout: ${SERVER_REPO}"

# The checkouts must BE the named commits. This is what makes the run
# independent of the caller's current branch.
SERVER_HEAD="$(git -C "${SERVER_REPO}" rev-parse HEAD)"
[[ "${SERVER_HEAD}" == "${SERVER_SOURCE}" ]] \
  || die "server checkout ${SERVER_REPO} is at ${SERVER_HEAD}, not the named --server-source ${SERVER_SOURCE}"
WEB_HEAD="$(git -C "${WEB_REPO}" rev-parse HEAD)"
if [[ "${WEB_HEAD}" != "${WEB_SOURCE}" ]]; then
  # A TEST-ONLY DESCENDANT IS ALLOWED, AND NOTHING ELSE IS.
  #
  # The claim this gate makes about the web repository is "the image bundles web
  # commit ${WEB_SOURCE}", and the BUNDLED WEB PROVENANCE layer proves it against
  # the image's own labels — not against whatever is checked out. What the
  # checkout supplies is the Playwright suite that DRIVES the image.
  #
  # Requiring HEAD == --web-source made those two things inseparable, so a
  # commit that adds a spec and changes no shipped byte could not be run against
  # the image it was written for without republishing the image first. That is
  # the opposite of what the release process wants: tesserafin-web #54's
  # publication rule says a test-only change needs no new image.
  #
  # So HEAD may move ahead of --web-source on one condition, checked below and
  # printed in full: every path that differs must be outside the production
  # build's inputs. `src/`, the webpack configs, the lockfile, the Node pin and
  # `config.json` are hard-excluded. `package.json` is compared key by key with
  # only `scripts` ignored — a version bump or a dependency change there DOES
  # alter shipped bytes and still fails.
  git -C "${WEB_REPO}" merge-base --is-ancestor "${WEB_SOURCE}" "${WEB_HEAD}" 2>/dev/null \
    || die "web checkout ${WEB_REPO} is at ${WEB_HEAD}, which is not the named --web-source ${WEB_SOURCE} nor a descendant of it"
  WEB_DIFF="$(git -C "${WEB_REPO}" diff --name-only "${WEB_SOURCE}" "${WEB_HEAD}")"
  echo "  web checkout is ${WEB_HEAD}, ahead of the bundled ${WEB_SOURCE}; changed paths:"
  echo "${WEB_DIFF}" | sed 's/^/    /'
  PRODUCTION_TOUCHED="$(printf '%s\n' "${WEB_DIFF}" | grep -E '^(src/|webpack\.|package-lock\.json$|\.nvmrc$|config\.json$)' || true)"
  [[ -z "${PRODUCTION_TOUCHED}" ]] \
    || die "the web checkout is ahead of --web-source and changes production build inputs, so the image no longer bundles it: ${PRODUCTION_TOUCHED//$'\n'/, }"
  if printf '%s\n' "${WEB_DIFF}" | grep -qx 'package.json'; then
    git -C "${WEB_REPO}" show "${WEB_SOURCE}:package.json" > "${TMPDIR:-/tmp}/pair-pkg-base.$$.json"
    git -C "${WEB_REPO}" show "${WEB_HEAD}:package.json"   > "${TMPDIR:-/tmp}/pair-pkg-head.$$.json"
    python3 - "${TMPDIR:-/tmp}/pair-pkg-base.$$.json" "${TMPDIR:-/tmp}/pair-pkg-head.$$.json" <<'PY' \
      || die "package.json differs outside its \"scripts\" block between ${WEB_SOURCE} and ${WEB_HEAD}; that can change shipped bytes"
import json, sys
a = json.load(open(sys.argv[1])); b = json.load(open(sys.argv[2]))
a.pop("scripts", None); b.pop("scripts", None)
sys.exit(0 if a == b else 1)
PY
    rm -f "${TMPDIR:-/tmp}/pair-pkg-base.$$.json" "${TMPDIR:-/tmp}/pair-pkg-head.$$.json"
  fi
  pass "the web checkout is a TEST-ONLY descendant of the bundled web commit (no production build input differs)"
else
  pass "both checkouts are at the named commits (no reliance on the current branch)"
fi

for tool in docker curl python3 ffmpeg ffprobe npm git; do
  command -v "${tool}" >/dev/null 2>&1 || die "${tool} is required but is not on PATH"
done
pass "required tooling present"

WORK="$(mktemp -d -t tesserafin-release-pair-XXXXXXXX)"
echo "  server image  : ${SERVER_IMAGE}"
echo "  server source : ${SERVER_SOURCE}  (${SERVER_REPO})"
echo "  web source    : ${WEB_SOURCE}  (${WEB_REPO})"
echo "  work tree     : ${WORK}"

docker image inspect "${SERVER_IMAGE}" >/dev/null 2>&1 || docker pull "${SERVER_IMAGE}" >/dev/null

# ---------------------------------------------------------------------------
# LAYER 1 — image provenance. Delegated to the A6 verifier; not re-implemented.
# Covers: OCI revision == SERVER_SOURCE_SHA, and the agreement of the image
# version, the application-reported version, /health's version and the OCI
# version label, plus /health actually reaching its readiness contract.
# ---------------------------------------------------------------------------
layer "IMAGE PROVENANCE"
VERIFY_PORT="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
if "${SERVER_REPO}/docker/version-verify.sh" "${SERVER_IMAGE}" "${VERIFY_PORT}" \
      --require-digest --expect-commit "${SERVER_SOURCE}" 2>&1 | sed 's/^/  | /'; then
  pass "docker/version-verify.sh agrees: revision, version surfaces and /health readiness"
else
  fail "docker/version-verify.sh rejected the image (see its assertions above)"
fi

# ---------------------------------------------------------------------------
# LAYER 2 — bundled web provenance.
#
# The point of this layer is that the image's own metadata agreeing WITH ITSELF
# proves nothing: a build can stamp a consistent but wrong commit. So each value
# is compared to the named tesserafin-web commit and to a real checkout of it.
# ---------------------------------------------------------------------------
layer "BUNDLED WEB PROVENANCE"

label() { docker inspect -f "{{index .Config.Labels \"$1\"}}" "${SERVER_IMAGE}"; }

WEB_REV_LABEL="$(label org.tesserafin.web.revision)"
WEB_VER_LABEL="$(label org.tesserafin.web.version)"
WEB_ASSETS_LABEL="$(label org.tesserafin.web.assets.image)"

docker run --rm --entrypoint cat "${SERVER_IMAGE}" /opt/tesserafin-web.revision.json \
  > "${WORK}/web-revision.json" 2>/dev/null \
  || die "the image carries no /opt/tesserafin-web.revision.json — it does not bundle a web client"
WEB_REV_FILE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["revision"])' "${WORK}/web-revision.json")"

echo "  label  org.tesserafin.web.revision     : ${WEB_REV_LABEL}"
echo "  label  org.tesserafin.web.version      : ${WEB_VER_LABEL}"
echo "  label  org.tesserafin.web.assets.image : ${WEB_ASSETS_LABEL}"
echo "  image  /opt/tesserafin-web.revision.json revision : ${WEB_REV_FILE}"

# (5) label and in-image metadata agree with each other …
[[ "${WEB_REV_LABEL}" == "${WEB_REV_FILE}" ]] \
  && pass "the OCI web revision label equals the in-image revision file" \
  || fail "OCI label web revision '${WEB_REV_LABEL}' disagrees with the in-image file '${WEB_REV_FILE}'"

# (6) … AND both name the intended commit. This is the assertion that catches a
# self-consistent image built from the wrong web tree.
[[ "${WEB_REV_LABEL}" == "${WEB_SOURCE}" ]] \
  && pass "the bundled web revision equals the named --web-source" \
  || fail "the bundled web revision '${WEB_REV_LABEL}' is not the named web source '${WEB_SOURCE}'"

git -C "${WEB_REPO}" cat-file -e "${WEB_SOURCE}^{commit}" 2>/dev/null \
  && pass "the named web commit exists in the web checkout" \
  || fail "commit ${WEB_SOURCE} does not exist in ${WEB_REPO}"

[[ -n "${WEB_VER_LABEL}" ]] \
  && pass "the image records a bundled web version (${WEB_VER_LABEL})" \
  || fail "org.tesserafin.web.version is empty"

# (7) the web-assets input is itself immutable.
[[ "${WEB_ASSETS_LABEL}" == *"@sha256:"* ]] \
  && pass "the bundled web-assets image is pinned by manifest digest" \
  || fail "org.tesserafin.web.assets.image '${WEB_ASSETS_LABEL}' is not a digest reference"

# The Dockerfile of the named server source must declare the same pin the image
# carries — otherwise the image and the source that claims to build it disagree.
DOCKERFILE_WEB_REF="$(git -C "${SERVER_REPO}" show "${SERVER_SOURCE}:Dockerfile" \
  | sed -n 's/^ARG WEB_VCS_REF=\(.*\)$/\1/p' | head -1)"
[[ "${DOCKERFILE_WEB_REF}" == "${WEB_SOURCE}" ]] \
  && pass "the server source's Dockerfile pins the same web commit (WEB_VCS_REF)" \
  || fail "Dockerfile WEB_VCS_REF '${DOCKERFILE_WEB_REF}' is not the named web source '${WEB_SOURCE}'"

# Every architecture in the manifest must declare the SAME bundled web revision.
# The Dockerfile pulls the web assets with `FROM --platform=linux/amd64` exactly
# so the two server images copy byte-identical web bytes; nothing enforced that
# claim against the published manifest until here. Labels only — no arm64
# container is booted, and no functional arm64 claim is made anywhere.
docker buildx imagetools inspect "${SERVER_IMAGE}" --raw > "${WORK}/manifest.json" 2>/dev/null \
  || die "could not read the manifest list for ${SERVER_IMAGE}"
python3 - "${WORK}/manifest.json" > "${WORK}/arch-digests.txt" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
for d in m.get("manifests", []):
    p = d.get("platform", {})
    if p.get("os") == "unknown" or p.get("architecture") == "unknown":
        continue          # attestation manifests, not runnable images
    print(f"{p.get('os')}/{p.get('architecture')}", d["digest"])
PY
sed 's/^/  arch: /' "${WORK}/arch-digests.txt"
REPO_REF="${SERVER_IMAGE%@*}"
ARCH_COUNT=0
while read -r platform digest; do
  [[ -n "${platform}" ]] || continue
  ARCH_COUNT=$((ARCH_COUNT + 1))
  arch_rev="$(docker buildx imagetools inspect "${REPO_REF}@${digest}" \
    --format '{{ index .Image.Config.Labels "org.tesserafin.web.revision" }}' 2>/dev/null || echo "")"
  [[ "${arch_rev}" == "${WEB_SOURCE}" ]] \
    && pass "${platform} (${digest}) declares the named bundled web revision" \
    || fail "${platform} declares bundled web revision '${arch_rev}', not '${WEB_SOURCE}'"
done < "${WORK}/arch-digests.txt"
[[ "${ARCH_COUNT}" -ge 2 ]] \
  && pass "the manifest list carries ${ARCH_COUNT} runnable architectures" \
  || fail "the manifest list carries only ${ARCH_COUNT} runnable architecture(s); a multi-arch release needs at least amd64 and arm64"

# ---------------------------------------------------------------------------
# LAYER 3 — OpenAPI.
#
# (8) the contract the server source GENERATES equals the committed canonical
# document. Comparing two committed files to each other would prove only that
# nobody edited them.
# ---------------------------------------------------------------------------
layer "OPENAPI"

COMMITTED_SPEC_SHA="$(git -C "${SERVER_REPO}" show "${SERVER_SOURCE}:openapi/openapi.json" | sha256sum | cut -d' ' -f1)"
LOCK_SHA="$(git -C "${SERVER_REPO}" show "${SERVER_SOURCE}:openapi/contract.lock.json" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["sha256"])')"
echo "  committed openapi/openapi.json sha256 : ${COMMITTED_SPEC_SHA}"
[[ "${LOCK_SHA}" == "${COMMITTED_SPEC_SHA}" ]] \
  && pass "openapi/contract.lock.json pins the committed contract" \
  || fail "contract.lock.json sha256 '${LOCK_SHA}' does not match the committed spec '${COMMITTED_SPEC_SHA}'"

if [[ "${SKIP_OPENAPI_REGEN}" -eq 1 ]]; then
  fail "DEGRADED: --skip-openapi-regen was passed; the contract was NOT regenerated from source"
else
  [[ -z "$(git -C "${SERVER_REPO}" status --porcelain -- openapi/)" ]] \
    || die "openapi/ is dirty in ${SERVER_REPO}; a regeneration diff could not be attributed"
  echo "  regenerating the contract from ${SERVER_SOURCE} (this builds and runs the server) …"
  if ( cd "${SERVER_REPO}" && ./ci/openapi-generate.sh ) > "${WORK}/openapi-generate.log" 2>&1; then
    GENERATED_SPEC_SHA="$(sha256sum "${SERVER_REPO}/openapi/openapi.json" | cut -d' ' -f1)"
    echo "  generated openapi/openapi.json sha256 : ${GENERATED_SPEC_SHA}"
    if [[ -z "$(git -C "${SERVER_REPO}" status --porcelain -- openapi/)" ]]; then
      pass "the regenerated contract is byte-identical to the committed canonical document"
    else
      fail "regenerating the contract changed openapi/ — the committed document is stale"
      git -C "${SERVER_REPO}" status --porcelain -- openapi/ | sed 's/^/    /'
    fi
    [[ "${GENERATED_SPEC_SHA}" == "${COMMITTED_SPEC_SHA}" ]] \
      && pass "generated sha256 == committed sha256" \
      || fail "generated sha256 '${GENERATED_SPEC_SHA}' != committed sha256 '${COMMITTED_SPEC_SHA}'"
  else
    fail "contract regeneration failed; see ${WORK}/openapi-generate.log"
    tail -20 "${WORK}/openapi-generate.log" | sed 's/^/    /'
  fi
fi

# ---------------------------------------------------------------------------
# LAYER 4 — generated SDK.
#
# (9) the contract tesserafin-web pinned is the contract this server serves, and
# (10) regenerating the SDK from it produces no drift. Both are asserted by the
# web repository's own gate; passing TESSERAFIN_SERVER_REPO is what upgrades it
# from "the SDK matches its pin" to "the pin matches the server".
# ---------------------------------------------------------------------------
layer "GENERATED SDK"

[[ -d "${WEB_REPO}/node_modules" ]] \
  || die "${WEB_REPO}/node_modules is absent — run 'npm ci' in the web checkout first"

if ( cd "${WEB_REPO}" && TESSERAFIN_SERVER_REPO="${SERVER_REPO}" \
      npm run --silent verify:tesserafin-sdk-fresh ) > "${WORK}/sdk-fresh.log" 2>&1; then
  grep -E '^\[verify:tesserafin-sdk-fresh\]' "${WORK}/sdk-fresh.log" | sed 's/^/  | /'
  pass "regenerating the web SDK produced zero drift"
else
  grep -E '^\[verify:tesserafin-sdk-fresh\]' "${WORK}/sdk-fresh.log" | sed 's/^/  | /' || true
  fail "the web SDK freshness gate failed; see ${WORK}/sdk-fresh.log"
fi

# The web gate compares the pin against the canonical contract AT THE COMMIT THE
# PIN NAMES. That commit may legitimately be older than the release commit — but
# only if the contract has not moved since. Assert exactly that, rather than
# accepting "an ancestor" as good enough.
PIN_SOURCE_COMMIT="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sourceCommit"])' \
  "${WEB_REPO}/src/lib/tesserafin-sdk/spec/version.json")"
echo "  web SDK provenance sourceCommit : ${PIN_SOURCE_COMMIT}"
if git -C "${SERVER_REPO}" cat-file -e "${PIN_SOURCE_COMMIT}^{commit}" 2>/dev/null; then
  PIN_SPEC_SHA="$(git -C "${SERVER_REPO}" show "${PIN_SOURCE_COMMIT}:openapi/openapi.json" | sha256sum | cut -d' ' -f1)"
  [[ "${PIN_SPEC_SHA}" == "${COMMITTED_SPEC_SHA}" ]] \
    && pass "the contract at the SDK's provenance commit is byte-identical to the release contract" \
    || fail "the SDK was generated from a DIFFERENT contract: ${PIN_SOURCE_COMMIT} carries ${PIN_SPEC_SHA}, the release carries ${COMMITTED_SPEC_SHA}"
  git -C "${SERVER_REPO}" merge-base --is-ancestor "${PIN_SOURCE_COMMIT}" "${SERVER_SOURCE}" \
    && pass "the SDK provenance commit is an ancestor of the release commit" \
    || fail "the SDK provenance commit ${PIN_SOURCE_COMMIT} is not an ancestor of ${SERVER_SOURCE}"
else
  fail "the SDK provenance commit ${PIN_SOURCE_COMMIT} does not exist in the server repository"
fi

# ---------------------------------------------------------------------------
# LAYER 5 — browser reachability of the bundled client.
#
# (12) delegated to the A1.2/A3 gate, which drives a real browser through the
# first-run wizard against pristine volumes and refuses an API-only image.
# ---------------------------------------------------------------------------
layer "BROWSER E2E"

if [[ "${SKIP_E2E}" -eq 1 ]]; then
  fail "DEGRADED: --skip-e2e was passed; neither browser reachability nor the lifecycle contract was proven"
else
  if ( cd "${SERVER_REPO}" && ./docker/browser-onboarding.sh "${SERVER_IMAGE}" ) \
        > "${WORK}/browser-onboarding.log" 2>&1; then
    grep -E '^\s+(PASS|FAIL)' "${WORK}/browser-onboarding.log" | tail -25 | sed 's/^/  | /'
    pass "docker/browser-onboarding.sh: the bundled web client is browser-reachable and onboardable"
  else
    grep -E '^\s+FAIL|ABORT' "${WORK}/browser-onboarding.log" | sed 's/^/  | /' || true
    fail "docker/browser-onboarding.sh failed; see ${WORK}/browser-onboarding.log"
  fi
fi

# ---------------------------------------------------------------------------
# LAYER 6 — lifecycle contract.
#
# (13) the tesserafin-web contract specs, run in a real browser against the
# release image by digest. The fixtures mirror ci/serve-e2e.sh's cross-repo
# contract (two movies, "Smoke Test Movie" sorting before "Transcode Probe",
# the H264-in-MP4 one being the only legitimate DirectPlay subject).
# ---------------------------------------------------------------------------

E2E_USER="tfpair"
E2E_PASSWORD="tfpairpass123"
# The restricted (non-admin, Movies-only) fixture user. tesserafin-web's
# library.spec.ts defaults to exactly these two values, and ci/serve-e2e.sh
# seeds them under the same names; keeping all three in step is what lets the
# same suite run against the source rig and against the release image.
RESTRICTED_USER="smokerestricted"
RESTRICTED_PASSWORD="restrictedpass123"
# Resolved by seed_instance(), consumed by run_lifecycle_round().
RESTRICTED_LIBRARY_ID=""
AUTH_HEADER='MediaBrowser Client="Tesserafin Release Pair", Device="ci/verify-release-pair.sh", DeviceId="tesserafin-release-pair", Version="0.0.0"'

# The four fixtures ci/serve-e2e.sh synthesizes, with the SAME titles and the
# SAME library split. That is a cross-repository contract, not a convention:
# tesserafin-web's specs resolve "Smoke Test Movie", "Transcode Probe" and
# "Remux Probe" BY NAME, and rely on the .srt sidecar for the external-subtitle
# case. Seeding a subset does not weaken the gate, it breaks it — a missing
# fixture surfaces as a product-shaped failure ("fixture not found on the
# server") that is really a harness defect.
synthesize_fixtures() { # $1 = movies root, $2 = probes root
  local movies="$1" probes="$2"
  local dp="${movies}/Smoke Test Movie (2020)"
  local tp="${movies}/Transcode Probe (2021)"
  local rp="${probes}/Remux Probe (2022)"
  mkdir -p "${dp}" "${tp}" "${rp}"
  # 1. DIRECT PLAY: H264 + AAC in MP4 — the only fixture whose first plan may
  #    legitimately be DirectPlay.
  ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "testsrc=size=320x240:rate=15:duration=2" \
    -f lavfi -i "sine=frequency=1000:duration=2" \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
    -c:a aac -movflags +faststart \
    "${dp}/Smoke Test Movie (2020).mp4"
  # 2. EXTERNAL SUBTITLE: a SubRip sidecar beside fixture 1. The filename must
  #    start with the video's own basename for MediaInfoResolver to attach it,
  #    and the cues must fall inside the 2 s runtime — a cue-less file probes as
  #    zero streams and attaches nothing.
  cat > "${dp}/Smoke Test Movie (2020).en.srt" <<'SRT'
1
00:00:00,200 --> 00:00:01,000
Tesserafin E2E external subtitle, cue one.

2
00:00:01,000 --> 00:00:01,900
Tesserafin E2E external subtitle, cue two.
SRT
  # 3. TRANSCODE: MPEG-4 Part 2 + AC-3. Deliberately NOT h264/aac — identical
  #    codecs are what made this a transcode fixture in name only.
  ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "testsrc=size=320x240:rate=15:duration=2" \
    -f lavfi -i "sine=frequency=1000:duration=2" \
    -c:v mpeg4 -pix_fmt yuv420p \
    -c:a ac3 -b:a 96k -movflags +faststart \
    "${tp}/Transcode Probe (2021).mp4"
  # 4. REMUX: the SAME elementary streams as fixture 1, rewrapped as Matroska.
  #    `-c copy` is the point: no re-encode, so only the container differs.
  ffmpeg -hide_banner -loglevel error -y -i "${dp}/Smoke Test Movie (2020).mp4" \
    -c copy -f matroska "${rp}/Remux Probe (2022).mkv"

  probe() { ffprobe -v error -select_streams "$2" -show_entries stream=codec_name -of csv=p=0 "$1"; }
  [[ "$(probe "${dp}/Smoke Test Movie (2020).mp4" v:0)" == "h264" ]] \
    || die "the DirectPlay fixture is not H264"
  [[ "$(probe "${tp}/Transcode Probe (2021).mp4" v:0)" == "mpeg4" ]] \
    || die "the transcode fixture is not MPEG-4 Part 2"
  [[ "$(probe "${rp}/Remux Probe (2022).mkv" v:0)" == "h264" ]] \
    || die "the remux fixture is not H264"
  [[ "$(probe "${dp}/Smoke Test Movie (2020).en.srt" s:0)" == "subrip" ]] \
    || die "the external subtitle fixture carries no SubRip stream"
  # format_name is "matroska,webm" for both; H264/AAC are not legal WebM codecs,
  # so the codec assertion above is what distinguishes them.
  ffprobe -v error -show_entries format=format_name -of csv=p=0 "${rp}/Remux Probe (2022).mkv" \
    | grep -q matroska || die "the remux fixture is not Matroska"
}

urlenc() { python3 -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"; }

wait_for_web() { # $1 = base url, $2 = container name
  local base="$1" cname="$2"
  for _ in $(seq 1 150); do
    # Readiness is the WEB CLIENT, not the API: /System/Info/Public is answered
    # by the startup SetupServer long before the application is up.
    if [[ "$(curl -s -o /dev/null -w '%{http_code}' "${base}/")" == "302" ]] \
       && curl -fsS "${base}/System/Info/Public" >/dev/null 2>&1; then
      return 0
    fi
    docker ps -q --filter "name=${cname}" | grep -q . || {
      echo "  container ${cname} exited early:"; docker logs "${cname}" 2>&1 | tail -30; return 1; }
    sleep 2
  done
  return 1
}

# ---------------------------------------------------------------------------
# The restricted-user fixture (tesserafin-web #54 / B1).
#
# WHY IT IS HERE NOW. This script's seeder was written for the three
# contract-critical playback specs, which need an admin and two libraries and
# nothing else. Running the FULL tesserafin-web suite against the release image
# — which is what B1 asks for — brought in library.spec.ts's
# "library access restriction (restricted user)" describe, and it needs a
# second, NON-admin user who can see Movies and cannot see Codec Probes, plus
# the withheld library's id. Without them the whole describe fails on a 401 and
# 1 further test never runs, on a perfectly healthy image. Observed on the B1
# image-backed run of 2026-07-27.
#
# This is a faithful port of ci/serve-e2e.sh's own restricted-user block, and
# deliberately so: the two rigs must agree, or the same spec means two different
# things depending on which one ran it. See that file for the long-form
# rationale on each call. Everything goes through the real public API.
#
# The fixture's contract is asserted here, not merely set up: through the
# restricted user's own /UserViews, movies must be present and homevideos must
# be absent. A half-applied policy otherwise surfaces as a confusing failure in
# the browser spec instead of a clear one here.
# ---------------------------------------------------------------------------
seed_restricted_user() { # $1 = base url, $2 = admin token -> 0 ok / 1 fail
  local base="$1" admin="$2"
  local auth_admin="${AUTH_HEADER}, Token=\"${admin}\""
  local views movies_view probes_view rid rtoken rviews

  views="$(curl -fsS "${base}/UserViews" -H "Authorization: ${auth_admin}")" || {
    echo "  restricted fixture: /UserViews (admin) failed" >&2; return 1; }
  movies_view="$(printf '%s' "${views}" | python3 -c '
import json,sys
items = json.load(sys.stdin).get("Items", [])
print(next((i["Id"] for i in items if i.get("CollectionType") == "movies"), ""))')"
  probes_view="$(printf '%s' "${views}" | python3 -c '
import json,sys
items = json.load(sys.stdin).get("Items", [])
print(next((i["Id"] for i in items if i.get("CollectionType") == "homevideos"), ""))')"
  [[ -n "${movies_view}" && -n "${probes_view}" ]] || {
    echo "  restricted fixture: could not resolve both view ids (movies='${movies_view}' homevideos='${probes_view}')" >&2
    return 1; }

  rid="$(curl -fsS -X POST "${base}/Users/New" -H 'Content-Type: application/json' \
    -H "Authorization: ${auth_admin}" \
    -d "{\"Name\":\"${RESTRICTED_USER}\",\"Password\":\"${RESTRICTED_PASSWORD}\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["Id"])')" || {
    echo "  restricted fixture: POST /Users/New failed" >&2; return 1; }

  # The user's REAL current policy, mutated only where the restriction lives.
  # Posting a hand-built partial policy would reset every unmentioned field to
  # its serializer default.
  local policy
  policy="$(curl -fsS "${base}/Users/${rid}" -H "Authorization: ${auth_admin}" \
    | TESSERAFIN_MOVIES_VIEW_ID="${movies_view}" python3 -c '
import json, os, sys
policy = json.load(sys.stdin)["Policy"]
policy["IsAdministrator"] = False
policy["EnableAllFolders"] = False
policy["EnabledFolders"] = [os.environ["TESSERAFIN_MOVIES_VIEW_ID"]]
print(json.dumps(policy))')" || { echo "  restricted fixture: GET /Users/{id} failed" >&2; return 1; }
  curl -fsS -X POST "${base}/Users/${rid}/Policy" -H 'Content-Type: application/json' \
    -H "Authorization: ${auth_admin}" -d "${policy}" >/dev/null || {
    echo "  restricted fixture: POST /Users/{id}/Policy failed" >&2; return 1; }

  rtoken="$(curl -fsS -X POST "${base}/Users/AuthenticateByName" -H 'Content-Type: application/json' \
    -H "Authorization: ${AUTH_HEADER}" \
    -d "{\"Username\":\"${RESTRICTED_USER}\",\"Pw\":\"${RESTRICTED_PASSWORD}\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["AccessToken"])')" || {
    echo "  restricted fixture: the credentials the specs use do not authenticate" >&2; return 1; }

  # The contract, through the restricted user's own eyes. Polled briefly: the
  # policy write is synchronous but the view endpoint shares the same caches as
  # everything else.
  local ok=0
  for _ in $(seq 1 30); do
    rviews="$(curl -fsS "${base}/UserViews?userId=${rid}" \
      -H "Authorization: ${AUTH_HEADER}, Token=\"${rtoken}\"" 2>/dev/null || echo '{}')"
    if printf '%s' "${rviews}" | python3 -c '
import json,sys
types = [i.get("CollectionType") for i in json.load(sys.stdin).get("Items", [])]
sys.exit(0 if "movies" in types and "homevideos" not in types else 1)' 2>/dev/null; then
      ok=1
      break
    fi
    sleep 1
  done
  [[ "${ok}" -eq 1 ]] || {
    echo "  restricted fixture: the policy never took — the restricted user must see movies and not homevideos" >&2
    return 1; }

  RESTRICTED_LIBRARY_ID="${probes_view}"
  echo "  seeded: restricted user '${RESTRICTED_USER}' (Movies only; withheld library ${RESTRICTED_LIBRARY_ID})"
  return 0
}

seed_instance() { # $1 = base url, $2 = scratch dir
  local base="$1" scratch="$2" token
  curl -fsS "${base}/Startup/User" -H "Authorization: ${AUTH_HEADER}" >/dev/null
  curl -fsS -X POST "${base}/Startup/User" -H 'Content-Type: application/json' \
    -H "Authorization: ${AUTH_HEADER}" \
    -d "{\"Name\":\"${E2E_USER}\",\"Password\":\"${E2E_PASSWORD}\"}" >/dev/null
  curl -fsS -X POST "${base}/Startup/Complete" -H "Authorization: ${AUTH_HEADER}" >/dev/null
  token="$(curl -fsS -X POST "${base}/Users/AuthenticateByName" -H 'Content-Type: application/json' \
    -H "Authorization: ${AUTH_HEADER}" \
    -d "{\"Username\":\"${E2E_USER}\",\"Pw\":\"${E2E_PASSWORD}\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["AccessToken"])')"
  add_library() { # <display name> <collectionType> <container path> <refresh>
    curl -fsS -X POST \
      "${base}/Library/VirtualFolders?name=$(urlenc "$1")&collectionType=$2&paths=$(urlenc "$3")&refreshLibrary=$4" \
      -H 'Content-Type: application/json' \
      -H "Authorization: ${AUTH_HEADER}, Token=\"${token}\"" \
      -d "{\"LibraryOptions\":{\"EnableRealtimeMonitor\":false,\"EnableChapterImageExtraction\":false,\"ExtractChapterImagesDuringLibraryScan\":false,\"PathInfos\":[{\"Path\":\"$3\"}]}}" >/dev/null
  }
  # EXACTLY ONE of these may request a refresh, and it must be the last: a
  # refresh scans the WHOLE library root, and two of them race and cancel each
  # other, which can leave a library with no items at all. Same rule, and the
  # same reason, as ci/serve-e2e.sh add_library().
  add_library Movies movies /media false
  add_library "Codec Probes" homevideos /probes true

  # "server up" is not "library visible": the scan is asynchronous.
  local userid movies remux
  userid="$(curl -fsS "${base}/Users/Me" -H "Authorization: ${AUTH_HEADER}, Token=\"${token}\"" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["Id"])')"
  for _ in $(seq 1 120); do
    movies="$(curl -fsS "${base}/Items?userId=${userid}&recursive=true&includeItemTypes=Movie" \
      -H "Authorization: ${AUTH_HEADER}, Token=\"${token}\"" \
      | python3 -c 'import json,sys; print(len(json.load(sys.stdin).get("Items",[])))')"
    remux="$(curl -fsS "${base}/Items?userId=${userid}&recursive=true&searchTerm=Remux%20Probe" \
      -H "Authorization: ${AUTH_HEADER}, Token=\"${token}\"" \
      | python3 -c 'import json,sys; print(len(json.load(sys.stdin).get("Items",[])))')"
    if [[ "${movies}" == "2" && "${remux}" -ge 1 ]]; then
      echo "  seeded: 2 movies + the Matroska remux probe"
      echo "${token}" > "${scratch}/token"
      seed_restricted_user "${base}" "${token}" || return 1
      return 0
    fi
    sleep 2
  done
  echo "  indexed ${movies:-0} movie(s) and ${remux:-0} remux probe(s) after the scan;" >&2
  echo "  the web specs need exactly 2 movies and the Matroska probe" >&2
  return 1
}

run_lifecycle_round() { # $1 = round number -> 0 pass / 1 fail
  local round="$1"
  local scratch="${WORK}/round-${round}"
  local cname="tesserafin-pair-${round}-$$"
  local port base
  mkdir -p "${scratch}/config" "${scratch}/cache" "${scratch}/data" "${scratch}/media" "${scratch}/probes"
  synthesize_fixtures "${scratch}/media" "${scratch}/probes"
  docker run --rm -v "${scratch}:/w" busybox chown -R 10000:10000 /w/config /w/cache /w/data >/dev/null
  # --port is honoured for a single round only. Reusing one fixed port across
  # rounds collides with a kept container from the previous round (--keep does
  # not remove it), so multi-round runs always take a fresh free port.
  if [[ -n "${PORT}" && "${LIFECYCLE_RUNS}" -eq 1 ]]; then
    port="${PORT}"
  else
    port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
  fi
  base="http://127.0.0.1:${port}"
  CONTAINERS+=("${cname}")
  docker run -d --name "${cname}" \
    -p "127.0.0.1:${port}:8096" \
    -v "${scratch}/config:/config" -v "${scratch}/cache:/cache" \
    -v "${scratch}/data:/data" \
    -v "${scratch}/media:/media:ro" -v "${scratch}/probes:/probes:ro" \
    "${SERVER_IMAGE}" >/dev/null
  echo "  round ${round}: container ${cname} on ${base} (fresh volumes, fresh fixtures)"
  wait_for_web "${base}" "${cname}" || { fail "round ${round}: the bundled web client was never served"; return 1; }
  seed_instance "${base}" "${scratch}" || { fail "round ${round}: seeding the instance failed"; return 1; }

  local rc=0
  # --output is not optional. Playwright CLEARS its artifact directory at the
  # start of every run, so leaving it at the repo default makes round N+1 delete
  # the traces, screenshots and error contexts of a failing round N — which is
  # precisely the evidence a flaky round has to be diagnosed from.
  ( cd "${WEB_REPO}" \
      && TESSERAFIN_E2E_BASE_URL="${base}" \
         TESSERAFIN_E2E_USER="${E2E_USER}" \
         TESSERAFIN_E2E_PASSWORD="${E2E_PASSWORD}" \
         TESSERAFIN_E2E_RESTRICTED_USER="${RESTRICTED_USER}" \
         TESSERAFIN_E2E_RESTRICTED_PASSWORD="${RESTRICTED_PASSWORD}" \
         TESSERAFIN_E2E_RESTRICTED_LIBRARY_ID="${RESTRICTED_LIBRARY_ID}" \
         PLAYWRIGHT_HTML_REPORT="${scratch}/playwright-report" \
         npx --no-install playwright test --output="${scratch}/test-results" "${E2E_SPECS[@]}" \
    ) > "${scratch}/e2e.log" 2>&1 || rc=$?
  docker logs "${cname}" > "${scratch}/server.log" 2>&1 || true
  tail -12 "${scratch}/e2e.log" | sed 's/^/  | /'
  if [[ "${rc}" -eq 0 ]]; then
    echo "  round ${round}: PASS"
  else
    echo "  round ${round}: FAIL (exit ${rc}) — evidence in ${scratch}"
  fi
  if [[ "${KEEP}" -eq 0 ]]; then
    docker rm -f "${cname}" >/dev/null 2>&1 || true
  fi
  return "${rc}"
}

declare -a ROUND_RESULTS=()
if [[ "${SKIP_E2E}" -eq 0 ]]; then
  layer "LIFECYCLE CONTRACT"
  echo "  specs: ${E2E_SPECS[*]}"
  for i in $(seq 1 "${LIFECYCLE_RUNS}"); do
    if run_lifecycle_round "${i}"; then
      ROUND_RESULTS+=("run ${i}: PASS")
    else
      ROUND_RESULTS+=("run ${i}: FAIL")
      fail "lifecycle run ${i} failed"
    fi
  done
  for r in "${ROUND_RESULTS[@]}"; do echo "  ${r}"; done
  [[ "${FAILED}" -eq 0 ]] && pass "${LIFECYCLE_RUNS} consecutive lifecycle run(s) green from clean fixture state"
fi

# ---------------------------------------------------------------------------
layer "SUMMARY"
if [[ "${FAILED}" -eq 0 ]]; then
  echo "  RESULT: PASS — the release pair is proven"
  echo "    server image  ${SERVER_IMAGE}"
  echo "    server source ${SERVER_SOURCE}"
  echo "    web source    ${WEB_SOURCE}"
  echo
  echo "  This is a LOCAL gate. It is not hosted CI and does not satisfy #97 / [C4]."
  exit 0
fi
echo "  RESULT: FAIL"
for f in "${FAILURES[@]}"; do echo "    - ${f}"; done
exit 1
