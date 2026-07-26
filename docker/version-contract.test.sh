#!/usr/bin/env bash
# shellcheck disable=SC2015
# SC2015: `<cond> && pass || fail` is intended — pass() and fail() both return 0,
#         so the `|| fail` branch never fires on a true condition.
# Unit + parity tests for docker/version-contract.sh (#92 / [A6]).
#
# Two kinds of assertion:
#   1. Behavioural — tag derivation and every fail-closed rule, run against a
#      throwaway git repository so the canonical version can be malformed on
#      purpose without touching the real tree.
#   2. Parity — `docker buildx bake --print` must emit byte-identical tags to
#      the contract. This is what makes "no duplicated tag logic" enforceable
#      rather than a comment. Skipped, loudly, when buildx is unavailable.
#
# Usage: docker/version-contract.test.sh
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONTRACT="${REPO_ROOT}/docker/version-contract.sh"
REG="ghcr.io/tesserafin-project/tesserafin"
COMMIT="1111111111111111111111111111111111111111"

PASSED=0; FAILED=0; SKIPPED=0
pass() { echo "  PASS  $*"; PASSED=$((PASSED + 1)); }
fail() { echo "  FAIL  $*"; FAILED=$((FAILED + 1)); }
skip() { echo "  SKIP  $*"; SKIPPED=$((SKIPPED + 1)); }

SANDBOX="$(mktemp -d)"
cleanup() { rm -rf "${SANDBOX}"; }
trap cleanup EXIT

# A minimal repo that owns a copy of the contract, so a deliberately broken
# SharedVersion.cs never appears in the real working tree. Each call gets its own
# mktemp directory: callers capture the path in a variable and keep using it after
# later calls, and a shared counter would not work anyway — command substitution
# runs this function in a subshell, so any increment it makes is discarded.
mk_sandbox() { # $1 = AssemblyVersion literal
  local dir
  dir="$(mktemp -d "${SANDBOX}/repoXXXXXX")"
  mkdir -p "${dir}/docker"
  cp "${CONTRACT}" "${dir}/docker/version-contract.sh"
  cat >"${dir}/SharedVersion.cs" <<EOF
using System.Reflection;

[assembly: AssemblyVersion("$1")]
[assembly: AssemblyFileVersion("$1")]
EOF
  git -C "${dir}" init -q
  git -C "${dir}" config user.email t@example.invalid
  git -C "${dir}" config user.name Test
  git -C "${dir}" add -A
  GIT_AUTHOR_DATE="@1700000000 +0000" GIT_COMMITTER_DATE="@1700000000 +0000" \
    git -C "${dir}" commit -qm init
  printf '%s\n' "${dir}"
}

# Runs the sandboxed contract, capturing stdout and the exit code.
run_sb() { # $1 = repo dir, rest = args
  local dir="$1"; shift
  ( cd "${dir}" && ./docker/version-contract.sh "$@" ) 2>"${SANDBOX}/err"
}

expect_ok() { # $1=label $2=repo $3=expected stdout, rest = args
  local label="$1" dir="$2" want="$3"; shift 3
  local got rc
  got="$(run_sb "${dir}" "$@")"; rc=$?
  if [[ "${rc}" -ne 0 ]]; then
    fail "${label}: exited ${rc} — $(head -1 "${SANDBOX}/err")"
  elif [[ "${got}" != "${want}" ]]; then
    fail "${label}: got [${got//$'\n'/ | }] want [${want//$'\n'/ | }]"
  else
    pass "${label}"
  fi
}

expect_fail() { # $1=label $2=repo $3=expected stderr substring, rest = args
  local label="$1" dir="$2" needle="$3"; shift 3
  local rc
  run_sb "${dir}" "$@" >/dev/null; rc=$?
  if [[ "${rc}" -eq 0 ]]; then
    fail "${label}: exited 0, expected non-zero"
  elif ! grep -qF -- "${needle}" "${SANDBOX}/err"; then
    fail "${label}: exited ${rc} but stderr lacks '${needle}' — $(head -1 "${SANDBOX}/err")"
  else
    pass "${label} (exit ${rc})"
  fi
}

echo "== 1. canonical version =="
GOOD="$(mk_sandbox 12.0.0)"
expect_ok "reads MAJOR.MINOR.PATCH from SharedVersion.cs" "${GOOD}" "12.0.0" version

for bad in "12.0" "12.0.0.0" "12.0.0-rc.1" "1.2.x" "01.0.0"; do
  BAD="$(mk_sandbox "${bad}")"
  expect_fail "rejects malformed canonical version '${bad}'" "${BAD}" \
    "is not a MAJOR.MINOR.PATCH SemVer core" version
done

echo "== 2. dev channel tags =="
expect_ok "dev derives the two immutable tags" "${GOOD}" \
  "${REG}:12.0.0-dev.111111111111
${REG}:sha-${COMMIT}" \
  tags --channel dev --commit "${COMMIT}"
expect_fail "dev refuses a --release-tag" "${GOOD}" \
  "--release-tag is not valid for the dev channel" \
  tags --channel dev --release-tag v12.0.0 --commit "${COMMIT}"

echo "== 3. prerelease channel tags =="
PRE="$(mk_sandbox 12.1.0)"
expect_ok "prerelease derives version + preview + sha, never latest" "${PRE}" \
  "${REG}:12.1.0-rc.1
${REG}:preview
${REG}:sha-${COMMIT}" \
  tags --channel prerelease --release-tag v12.1.0-rc.1 --commit "${COMMIT}"
expect_ok "prerelease accepts a tag without the v prefix" "${PRE}" \
  "${REG}:12.1.0-rc.1
${REG}:preview
${REG}:sha-${COMMIT}" \
  tags --channel prerelease --release-tag 12.1.0-rc.1 --commit "${COMMIT}"
expect_fail "prerelease refuses a stable tag" "${PRE}" \
  "has no pre-release identifier" \
  tags --channel prerelease --release-tag v12.1.0 --commit "${COMMIT}"
expect_fail "prerelease refuses a core that differs from the source" "${PRE}" \
  "!= canonical version '12.1.0'" \
  tags --channel prerelease --release-tag v13.0.0-rc.1 --commit "${COMMIT}"

echo "== 4. stable channel tags =="
expect_ok "stable derives version + minor + major + latest + sha" "${PRE}" \
  "${REG}:12.1.0
${REG}:12.1
${REG}:12
${REG}:latest
${REG}:sha-${COMMIT}" \
  tags --channel stable --release-tag v12.1.0 --commit "${COMMIT}"
expect_fail "stable refuses a pre-release tag (no latest from a pre-release)" "${PRE}" \
  "a pre-release must never publish stable or 'latest' tags" \
  tags --channel stable --release-tag v12.1.0-rc.1 --commit "${COMMIT}"
expect_fail "stable refuses a tag/source mismatch" "${PRE}" \
  "!= canonical version '12.1.0'" \
  tags --channel stable --release-tag v12.2.0 --commit "${COMMIT}"
expect_fail "stable refuses build metadata" "${PRE}" \
  "build metadata is not allowed" \
  tags --channel stable --release-tag v12.1.0+build.5 --commit "${COMMIT}"

echo "== 5. commit provenance =="
expect_fail "refuses a short commit" "${GOOD}" \
  "is not a full 40-character lowercase SHA" tags --commit deadbeef
expect_fail "refuses a non-hex commit" "${GOOD}" \
  "is not a full 40-character lowercase SHA" \
  tags --commit "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"
NOGIT="${SANDBOX}/nogit"; rm -rf "${NOGIT}"; mkdir -p "${NOGIT}/docker"
cp "${CONTRACT}" "${NOGIT}/docker/version-contract.sh"
cp "${GOOD}/SharedVersion.cs" "${NOGIT}/SharedVersion.cs"
expect_fail "refuses to invent provenance outside a git checkout" "${NOGIT}" \
  "missing commit provenance" tags

echo "== 6. dirty-tree release publication =="
DIRTY="$(mk_sandbox 12.1.0)"
echo "// local edit" >>"${DIRTY}/SharedVersion.cs"
expect_fail "refuses a stable release from a dirty tree" "${DIRTY}" \
  "refusing to derive stable tags from a dirty working tree" \
  tags --channel stable --release-tag v12.1.0 --commit "${COMMIT}"
expect_ok "--allow-dirty overrides, and dev builds are never blocked" "${DIRTY}" \
  "${REG}:12.1.0-dev.111111111111
${REG}:sha-${COMMIT}" \
  tags --channel dev --commit "${COMMIT}"
if run_sb "${DIRTY}" env --channel stable --release-tag v12.1.0 --allow-dirty \
     | grep -q '^TESSERAFIN_DIRTY_RELEASE=1$'; then
  pass "--allow-dirty records the override in the emitted environment"
else
  fail "--allow-dirty did not emit TESSERAFIN_DIRTY_RELEASE=1"
fi
if grep -q "WARNING — publishing release" "${SANDBOX}/err"; then
  pass "--allow-dirty prints a visible warning to stderr"
else
  fail "--allow-dirty printed no warning"
fi

echo "== 7. verify-tag =="
expect_ok "verify-tag accepts the matching stable tag" "${PRE}" \
  "OK: git tag 'v12.1.0' matches canonical version 12.1.0 (stable)" verify-tag v12.1.0
expect_ok "verify-tag accepts a matching pre-release tag" "${PRE}" \
  "OK: git tag 'v12.1.0-rc.2' is a PRE-RELEASE of canonical version 12.1.0 (must not move 'latest')" \
  verify-tag v12.1.0-rc.2
expect_fail "verify-tag rejects a tag that disagrees with SharedVersion.cs" "${PRE}" \
  "but SharedVersion.cs says '12.1.0'" verify-tag v12.9.9

echo "== 8. env output is deterministic and complete =="
# No --commit here: `env` reads the commit TIME, so provenance must be a commit
# that actually exists. The sandbox commit is pinned to epoch 1700000000.
ENV_OUT="$(run_sb "${GOOD}" env)"
for k in VERSION VCS_REF SOURCE_DATE_EPOCH BUILD_DATE REGISTRY CHANNEL TAGS PRIMARY_TAG; do
  grep -q "^${k}=" <<<"${ENV_OUT}" && pass "env emits ${k}" || fail "env is missing ${k}"
done
grep -q "^SOURCE_DATE_EPOCH=1700000000$" <<<"${ENV_OUT}" \
  && pass "env derives SOURCE_DATE_EPOCH from the commit, not the wall clock" \
  || fail "env SOURCE_DATE_EPOCH is not the commit time"
grep -q "^BUILD_DATE=2023-11-14T22:13:20Z$" <<<"${ENV_OUT}" \
  && pass "env derives BUILD_DATE from the commit time" \
  || fail "env BUILD_DATE is not the commit time"
[[ "$(run_sb "${GOOD}" env)" == "${ENV_OUT}" ]] \
  && pass "env output is byte-identical across runs" || fail "env output is not deterministic"

echo "== 9. bake parity: docker-bake.hcl adds no tag logic of its own =="
if ! command -v docker >/dev/null 2>&1 || ! docker buildx version >/dev/null 2>&1; then
  skip "docker buildx unavailable — bake parity not checked"
else
  parity_case() { # $1=label, rest = contract args
    local label="$1"; shift
    local env_out contract_tags bake_tags
    if ! env_out="$("${CONTRACT}" env "$@" 2>"${SANDBOX}/perr")"; then
      fail "${label}: contract failed — $(head -1 "${SANDBOX}/perr")"; return
    fi
    contract_tags="$("${CONTRACT}" tags "$@")"
    # SC2046: the split is deliberate — each KEY=VALUE line becomes one argument
    # to `env`. The contract only emits values without whitespace (a version, a
    # sha, an epoch, an RFC3339 stamp, comma-joined image refs).
    # shellcheck disable=SC2046
    bake_tags="$(
      env -i PATH="${PATH}" HOME="${HOME}" \
        $(printf '%s\n' "${env_out}" | grep -E '^(VERSION|VCS_REF|SOURCE_DATE_EPOCH|BUILD_DATE|TAGS)=') \
        docker buildx bake --file "${REPO_ROOT}/docker-bake.hcl" --print server 2>"${SANDBOX}/perr" \
        | python3 -c 'import sys,json; print("\n".join(json.load(sys.stdin)["target"]["server"]["tags"]))'
    )" || { fail "${label}: bake --print failed — $(tail -1 "${SANDBOX}/perr")"; return; }
    if [[ "${bake_tags}" == "${contract_tags}" ]]; then
      pass "${label}: bake tags == contract tags"
    else
      fail "${label}: bake [${bake_tags//$'\n'/ | }] != contract [${contract_tags//$'\n'/ | }]"
    fi
  }
  # Real repository, real HEAD — dev is the only channel that can be exercised
  # without inventing a release tag, so the release channels are driven with an
  # explicit --release-tag matching this tree's canonical version.
  REAL_VERSION="$("${CONTRACT}" version)"
  # --allow-dirty on the release channels only: this test is normally run from a
  # feature branch whose tree carries uncommitted work, and refusing there would
  # make parity unverifiable exactly when it matters most. The dev channel is
  # deliberately left strict.
  parity_case "dev channel"        --channel dev
  parity_case "prerelease channel" --channel prerelease --release-tag "v${REAL_VERSION}-rc.1" --allow-dirty
  parity_case "stable channel"     --channel stable     --release-tag "v${REAL_VERSION}"     --allow-dirty

  # An unset TAGS must not silently produce a plausible image reference.
  UNSET_TAGS="$(docker buildx bake --file "${REPO_ROOT}/docker-bake.hcl" --print server 2>/dev/null \
    | python3 -c 'import sys,json; print(json.load(sys.stdin)["target"]["server"].get("tags"))' 2>/dev/null || echo ERROR)"
  if [[ "${UNSET_TAGS}" == "['']" || "${UNSET_TAGS}" == "None" || "${UNSET_TAGS}" == "ERROR" ]]; then
    pass "bake without TAGS produces no usable tag (fail-closed)"
  else
    fail "bake without TAGS produced ${UNSET_TAGS}"
  fi
fi

echo
echo "version-contract tests: ${PASSED} passed, ${FAILED} failed, ${SKIPPED} skipped"
[[ "${FAILED}" -eq 0 ]]
