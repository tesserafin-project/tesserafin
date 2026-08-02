#!/usr/bin/env bash
#
# Cross-repository SDK provenance gate — the hosted half of C4 (#97, #164).
#
# WHAT THIS ADDS THAT NOTHING ELSE DOES. tesserafin-web already proves that its
# generated SDK is reproducible from the spec it has pinned, and that the pinned
# spec is attributable to a 40-character server commit. Both of those are
# self-referential: a pinned spec and a generated tree can agree with each other
# perfectly while both are stale relative to the server contract they claim to
# mirror. This script proves the third property, the one that lives in neither
# repository's automatic gates:
#
#   the canonical contract at the commit the web pin NAMES is byte-identical to
#   the canonical contract at the server commit being analysed.
#
# NO CROSS-REPOSITORY CREDENTIAL. The sibling repository is cloned over
# anonymous HTTPS — no PAT, no deploy key, no GitHub App, no custom secret, and
# not even $GITHUB_TOKEN. Both repositories are public; if that ever stops being
# true the clone fails and this gate goes red, which is the correct outcome.
#
# FAIL-CLOSED. Every exit path that is not "every property held" is non-zero.
# There is no skip, no soft warning that passes, and no "provenance UNVERIFIED"
# state: the whole point of running this hosted is that the degraded mode the
# web-side verifier falls back to when no server checkout is reachable can no
# longer happen.
#
# Exit codes: 0 verified, 1 a property failed, 2 the question was not answered
# (bad invocation, missing tool, clone failure).

set -uo pipefail

SERVER_REPO=""
LOCK=""
WORKDIR=""
SERVER_COMMIT=""
KEEP=0

die_indeterminate() { printf 'sdk-provenance: INDETERMINATE: %s\n' "$1" >&2; exit 2; }
die_fail()          { printf 'sdk-provenance: FAIL: %s\n' "$1" >&2; exit 1; }
ok()                { printf 'sdk-provenance: ok  — %s\n' "$1"; }

usage() {
    cat <<'EOF'
Usage: ci/verify-sdk-provenance.sh --server-repo PATH --workdir DIR [options]

  --server-repo PATH   server checkout to verify (must contain openapi/ and .git)
  --workdir DIR        empty directory for the anonymous web clone
  --lock PATH          pair lock (default: <server-repo>/ci/web-pair.lock.json)
  --server-commit SHA  server commit to verify against (default: the checkout HEAD)
  --keep               do not delete the workdir on exit

Exit codes: 0 verified, 1 property failed, 2 question not answered.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
    --server-repo)   SERVER_REPO="${2:?--server-repo needs a value}"; shift 2 ;;
    --workdir)       WORKDIR="${2:?--workdir needs a value}"; shift 2 ;;
    --lock)          LOCK="${2:?--lock needs a value}"; shift 2 ;;
    --server-commit) SERVER_COMMIT="${2:?--server-commit needs a value}"; shift 2 ;;
    --keep)          KEEP=1; shift ;;
    -h|--help)       usage; exit 0 ;;
    *)               die_indeterminate "unknown option $1" ;;
    esac
done

[ -n "$SERVER_REPO" ] || die_indeterminate "--server-repo is required"
[ -n "$WORKDIR" ]     || die_indeterminate "--workdir is required"
# `.git` is a directory in a clone and a file in a worktree; ask git instead.
git -C "$SERVER_REPO" rev-parse --git-dir >/dev/null 2>&1 \
    || die_indeterminate "$SERVER_REPO is not a git checkout"
LOCK="${LOCK:-$SERVER_REPO/ci/web-pair.lock.json}"
[ -f "$LOCK" ] || die_indeterminate "pair lock not found at $LOCK"

for tool in git jq node npm sha256sum; do
    command -v "$tool" >/dev/null 2>&1 || die_indeterminate "required tool '$tool' is not on PATH"
done

SERVER_REPO="$(cd "$SERVER_REPO" && pwd)"
mkdir -p "$WORKDIR" || die_indeterminate "cannot create $WORKDIR"
WORKDIR="$(cd "$WORKDIR" && pwd)"
[ -z "$(ls -A "$WORKDIR" 2>/dev/null)" ] || die_indeterminate "$WORKDIR is not empty"
cleanup() { [ "$KEEP" -eq 1 ] || rm -rf "$WORKDIR"; }
trap cleanup EXIT

if [ -z "$SERVER_COMMIT" ]; then
    SERVER_COMMIT="$(git -C "$SERVER_REPO" rev-parse HEAD 2>/dev/null)" \
        || die_indeterminate "cannot resolve HEAD in $SERVER_REPO"
fi
case "$SERVER_COMMIT" in
    *[!0-9a-f]* | "") die_fail "server commit '$SERVER_COMMIT' is not lowercase hexadecimal" ;;
esac
[ "${#SERVER_COMMIT}" -eq 40 ] || die_fail "server commit '$SERVER_COMMIT' is not a full 40-character SHA"
git -C "$SERVER_REPO" cat-file -e "$SERVER_COMMIT^{commit}" 2>/dev/null \
    || die_fail "server commit $SERVER_COMMIT does not exist in $SERVER_REPO"
ok "server commit under test: $SERVER_COMMIT"

# --------------------------------------------------------------------------
# 1. The lock. Immutable by construction: a full commit SHA, never a branch,
#    never a tag, and an exact repository name.
# --------------------------------------------------------------------------
jq -e . "$LOCK" >/dev/null 2>&1 || die_fail "pair lock $LOCK is not valid JSON"
WEB_REPOSITORY="$(jq -r '.webRepository // empty' "$LOCK")"
WEB_COMMIT="$(jq -r '.webCommit // empty' "$LOCK")"

[ -n "$WEB_REPOSITORY" ] || die_fail "pair lock records no webRepository"
[ -n "$WEB_COMMIT" ]     || die_fail "pair lock records no webCommit"
case "$WEB_REPOSITORY" in
    */*) : ;;
    *)   die_fail "webRepository '$WEB_REPOSITORY' is not an owner/name pair" ;;
esac
case "$WEB_COMMIT" in
    *[!0-9a-f]* | "") die_fail "webCommit '$WEB_COMMIT' is not lowercase hexadecimal — a branch name, a tag or an abbreviated SHA is never accepted here" ;;
esac
[ "${#WEB_COMMIT}" -eq 40 ] \
    || die_fail "webCommit '$WEB_COMMIT' is ${#WEB_COMMIT} characters; an abbreviated commit is not an immutable pin"
ok "pair lock: $WEB_REPOSITORY @ $WEB_COMMIT"

# --------------------------------------------------------------------------
# 2. Anonymous clone of the sibling repository. No credential of any kind.
# --------------------------------------------------------------------------
WEB="$WORKDIR/web"
CLONE_URL="https://github.com/$WEB_REPOSITORY.git"
if ! GIT_TERMINAL_PROMPT=0 GIT_ASKPASS=/bin/true GIT_CONFIG_GLOBAL=/dev/null \
        git -c credential.helper= -c http.extraheader= \
        clone --quiet "$CLONE_URL" "$WEB" 2>"$WORKDIR/clone.err"; then
    printf 'sdk-provenance: clone stderr: %s\n' "$(head -3 "$WORKDIR/clone.err" | tr '\n' ' ')" >&2
    die_fail "cannot clone $CLONE_URL anonymously — the repository is missing, renamed, or no longer public"
fi
ok "cloned $CLONE_URL anonymously (no PAT, no deploy key, no App, no GITHUB_TOKEN)"

git -C "$WEB" cat-file -e "$WEB_COMMIT^{commit}" 2>/dev/null \
    || die_fail "webCommit $WEB_COMMIT does not exist in $WEB_REPOSITORY"
git -C "$WEB" -c advice.detachedHead=false checkout --quiet "$WEB_COMMIT" \
    || die_fail "cannot check out $WEB_COMMIT in the web clone"
ok "web checkout at $WEB_COMMIT"

# --------------------------------------------------------------------------
# 3. Server contract against its own lock.
# --------------------------------------------------------------------------
CONTRACT_LOCK="$SERVER_REPO/openapi/contract.lock.json"
[ -f "$CONTRACT_LOCK" ] || die_fail "openapi/contract.lock.json is missing from the server checkout"
LOCKED_SPEC_PATH="$(jq -r '.spec // "openapi/openapi.json"' "$CONTRACT_LOCK")"
LOCKED_SHA="$(jq -r '.sha256 // empty' "$CONTRACT_LOCK")"
LOCKED_VERSION="$(jq -r '.version // empty' "$CONTRACT_LOCK")"
[ -n "$LOCKED_SHA" ] || die_fail "openapi/contract.lock.json records no sha256"

ACTUAL_CONTRACT_SHA="$(git -C "$SERVER_REPO" show "$SERVER_COMMIT:$LOCKED_SPEC_PATH" 2>/dev/null | sha256sum | cut -d' ' -f1)"
[ -n "$ACTUAL_CONTRACT_SHA" ] || die_fail "cannot read $LOCKED_SPEC_PATH at $SERVER_COMMIT"
[ "$ACTUAL_CONTRACT_SHA" = "$LOCKED_SHA" ] \
    || die_fail "canonical contract at $SERVER_COMMIT is sha256 $ACTUAL_CONTRACT_SHA but openapi/contract.lock.json records $LOCKED_SHA"
ok "server contract matches its lock (sha256 $LOCKED_SHA)"

# --------------------------------------------------------------------------
# 4. The web pin's provenance, read from the web checkout.
# --------------------------------------------------------------------------
VERSION_JSON="$WEB/src/lib/tesserafin-sdk/spec/version.json"
PINNED_SPEC="$WEB/src/lib/tesserafin-sdk/spec/openapi.json"
GENERATED_DIR="$WEB/src/lib/tesserafin-sdk/generated"
[ -f "$VERSION_JSON" ] || die_fail "web checkout has no src/lib/tesserafin-sdk/spec/version.json"
[ -f "$PINNED_SPEC" ]  || die_fail "web checkout has no src/lib/tesserafin-sdk/spec/openapi.json"
[ -d "$GENERATED_DIR" ] || die_fail "web checkout has no src/lib/tesserafin-sdk/generated/ tree"
jq -e . "$VERSION_JSON" >/dev/null 2>&1 || die_fail "web spec/version.json is not valid JSON"

SOURCE_COMMIT="$(jq -r '.sourceCommit // empty' "$VERSION_JSON")"
SPEC_SHA="$(jq -r '.specSha256 // empty' "$VERSION_JSON")"
PIN_VERSION="$(jq -r '.version // empty' "$VERSION_JSON")"

for field in sourceCommit specSha256 version; do
    jq -e --arg f "$field" 'has($f) and (.[$f] != null)' "$VERSION_JSON" >/dev/null \
        || die_fail "web spec/version.json records no $field"
done
case "$SOURCE_COMMIT" in
    *[!0-9a-f]* | "") die_fail "web sourceCommit '$SOURCE_COMMIT' is not lowercase hexadecimal" ;;
esac
[ "${#SOURCE_COMMIT}" -eq 40 ] \
    || die_fail "web sourceCommit '$SOURCE_COMMIT' is ${#SOURCE_COMMIT} characters; an abbreviated commit cannot be resolved unambiguously"

ACTUAL_SPEC_SHA="$(sha256sum "$PINNED_SPEC" | cut -d' ' -f1)"
[ "$ACTUAL_SPEC_SHA" = "$SPEC_SHA" ] \
    || die_fail "web pinned spec is sha256 $ACTUAL_SPEC_SHA but version.json records $SPEC_SHA — the pinned spec was edited by hand"
ok "web pin: sourceCommit $SOURCE_COMMIT, specSha256 verified against the bytes on disk"

if [ -n "$LOCKED_VERSION" ] && [ "$PIN_VERSION" != "$LOCKED_VERSION" ]; then
    die_fail "web version.json records version '$PIN_VERSION' but the server contract lock records '$LOCKED_VERSION'"
fi
ok "version.json version '$PIN_VERSION' agrees with the server contract lock"

# --------------------------------------------------------------------------
# 5. THE MISSING PROPERTY. The pin must resolve in the server repository, be an
#    ancestor of the commit under test, and name a commit whose canonical
#    contract is byte-identical to the one under test.
#
#    Being BEHIND is not by itself a failure: pinning an older contract on
#    purpose is legitimate, and moving the pin merely because it is behind would
#    be a change with no provenance reason. Being behind a contract that has
#    MOVED is a failure.
# --------------------------------------------------------------------------
git -C "$SERVER_REPO" cat-file -e "$SOURCE_COMMIT^{commit}" 2>/dev/null \
    || die_fail "web sourceCommit $SOURCE_COMMIT does not resolve in the server repository"
git -C "$SERVER_REPO" merge-base --is-ancestor "$SOURCE_COMMIT" "$SERVER_COMMIT" 2>/dev/null \
    || die_fail "web sourceCommit $SOURCE_COMMIT is NOT an ancestor of $SERVER_COMMIT — the pin names a commit that is not on this history"

PIN_CONTRACT_SHA="$(git -C "$SERVER_REPO" show "$SOURCE_COMMIT:$LOCKED_SPEC_PATH" 2>/dev/null | sha256sum | cut -d' ' -f1)"
[ -n "$PIN_CONTRACT_SHA" ] || die_fail "cannot read $LOCKED_SPEC_PATH at the pinned commit $SOURCE_COMMIT"
if [ "$PIN_CONTRACT_SHA" != "$ACTUAL_CONTRACT_SHA" ]; then
    BEHIND="$(git -C "$SERVER_REPO" rev-list --count "$SOURCE_COMMIT..$SERVER_COMMIT" 2>/dev/null || echo '?')"
    die_fail "the canonical contract MOVED after the web pin: sha256 $PIN_CONTRACT_SHA at $SOURCE_COMMIT versus $ACTUAL_CONTRACT_SHA at $SERVER_COMMIT ($BEHIND commits apart). Regenerate the web SDK against the current contract and re-pin; do not move the lock without regenerating."
fi
BEHIND="$(git -C "$SERVER_REPO" rev-list --count "$SOURCE_COMMIT..$SERVER_COMMIT" 2>/dev/null || echo '?')"
ok "canonical contract is byte-identical at the pin and at the commit under test ($BEHIND commits apart, contract unchanged)"

# --------------------------------------------------------------------------
# 6. Transform equality, SDK regeneration and generated-tree drift, executed by
#    the web repository's own verifier with a real server checkout in scope so
#    its degraded "provenance UNVERIFIED" path cannot be taken.
# --------------------------------------------------------------------------
( cd "$WEB" && npm ci --no-audit --no-fund ) >"$WORKDIR/npm-ci.log" 2>&1 \
    || { tail -20 "$WORKDIR/npm-ci.log" >&2; die_indeterminate "npm ci failed in the web checkout"; }
ok "web dependencies installed"

set +e
( cd "$WEB" && TESSERAFIN_SERVER_REPO="$SERVER_REPO" npm run --silent verify:tesserafin-sdk-fresh ) \
    >"$WORKDIR/verify.log" 2>&1
VERIFY_RC=$?
set -e
sed -n '1,200p' "$WORKDIR/verify.log"
if [ "$VERIFY_RC" -ne 0 ]; then
    die_fail "the web SDK freshness verifier failed (exit $VERIFY_RC) — see the output above"
fi
if grep -q 'UNVERIFIED' "$WORKDIR/verify.log"; then
    die_fail "the web verifier announced a degraded, UNVERIFIED provenance state despite a server checkout being supplied — a degraded run is never accepted as evidence here"
fi
if ! grep -q "matches the canonical contract at $SOURCE_COMMIT exactly" "$WORKDIR/verify.log"; then
    die_fail "the web verifier did not report the full canonical-contract comparison — the run is not proof of transform equality"
fi
ok "transform equality, SDK regeneration and generated-tree drift all verified by the web repository's own gate"

GENERATED_COUNT="$(find "$GENERATED_DIR" -type f | wc -l | tr -d ' ')"
[ "$GENERATED_COUNT" -gt 0 ] || die_fail "the generated SDK tree is empty"

cat <<EOF

sdk-provenance: VERIFIED
  server commit          $SERVER_COMMIT
  server contract sha256 $ACTUAL_CONTRACT_SHA
  web repository         $WEB_REPOSITORY
  web commit (locked)    $WEB_COMMIT
  web pin sourceCommit   $SOURCE_COMMIT ($BEHIND commits behind, contract byte-identical)
  web pinned spec sha256 $SPEC_SHA
  generated files        $GENERATED_COUNT
EOF
exit 0
