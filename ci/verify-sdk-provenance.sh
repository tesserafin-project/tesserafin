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
# PROVENANCE SCHEMA 2 (C4-LH, #246). This gate used to additionally require the
# web pin's `sourceCommit` to be a git ANCESTOR of the commit under test. On a
# branch with `required_linear_history` that is unsatisfiable for a contract
# change: before the merge the only commit carrying the new canonical bytes is on
# the pull request branch, and every merge method such a branch permits rewrites
# its SHA, so the pin is non-ancestral the moment it lands. The protocol demanded
# a state GitHub cannot produce.
#
# Schema 2 replaces ancestry with CONTENT. The web generator consumes exactly one
# thing from this repository — the canonical `openapi/openapi.json` bytes — so two
# server commits carrying byte-identical locked canonical contracts produce a
# byte-identical transport boundary regardless of commit identity. `sourceCommit`
# remains mandatory audit evidence: it must exist, resolve here, be a full SHA,
# and both its canonical bytes and its contract lock must match.
#
# This is not the old rule with a hole in it. A schema-2 pin proves strictly more
# than a schema-1 pin ever did — the recorded canonical digest, the source
# repository, the contract lock at the pinned commit, the transform pipeline
# version, the generator identity and a manifest of every generated file, all
# recomputed from bytes rather than read as assertions. Schema 1 keeps its
# ancestry requirement exactly as written; a schema-1 non-ancestor still fails.
#
# Those per-byte proofs live in `ci/verify-web-provenance.sh`, which this script
# calls on the checkout it cloned. See the header there for why they are split.
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

# Captured BEFORE the checkout below detaches HEAD: the branch a fresh clone lands on is the
# repository's default branch, and is the fallback for the merged-commit check further down.
WEB_CLONED_BRANCH="$(git -C "$WEB" rev-parse --abbrev-ref HEAD 2>/dev/null)"
[ "$WEB_CLONED_BRANCH" = "HEAD" ] && WEB_CLONED_BRANCH=""

git -C "$WEB" cat-file -e "$WEB_COMMIT^{commit}" 2>/dev/null \
    || die_fail "webCommit $WEB_COMMIT does not exist in $WEB_REPOSITORY"
git -C "$WEB" -c advice.detachedHead=false checkout --quiet "$WEB_COMMIT" \
    || die_fail "cannot check out $WEB_COMMIT in the web clone"
ok "web checkout at $WEB_COMMIT"

# The locked commit must be MERGED, not merely reachable. A pull request head is a real commit
# that resolves and checks out perfectly, so every content proof below would pass against one —
# and then the web repository squashes or rebases the branch, the head stops being on `main`, and
# the pair is locked to a commit no released web build was ever built from. Requiring the lock to
# name a commit on the web default branch is what makes "final merge commit" mean something.
#
# Read from `origin/HEAD` rather than a hard-coded name, so a default-branch rename is a clone-time
# fact rather than a silent skip. The branch the clone checked out is the same answer by a
# different route and is used only if `origin/HEAD` is somehow absent — never a hard-coded "main",
# because a wrong branch name here would compare against nothing and pass everything.
WEB_DEFAULT="$(git -C "$WEB" symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>/dev/null)"
if [ -z "$WEB_DEFAULT" ] && [ -n "$WEB_CLONED_BRANCH" ]; then
    WEB_DEFAULT="origin/$WEB_CLONED_BRANCH"
fi
[ -n "$WEB_DEFAULT" ] || die_indeterminate "cannot resolve the web repository's default branch"
git -C "$WEB" rev-parse --verify --quiet "refs/remotes/$WEB_DEFAULT^{commit}" >/dev/null \
    || die_indeterminate "the web repository's default branch $WEB_DEFAULT does not resolve in the clone"
git -C "$WEB" merge-base --is-ancestor "$WEB_COMMIT" "refs/remotes/$WEB_DEFAULT" 2>/dev/null \
    || die_fail "webCommit $WEB_COMMIT is not an ancestor of $WEB_DEFAULT — the lock names a commit that is not merged (a pull request head, or a branch that was squashed away). Pin the final merge commit."
ok "webCommit is merged into $WEB_DEFAULT"

# --------------------------------------------------------------------------
# 3. Server contract against its own lock.
# --------------------------------------------------------------------------
CONTRACT_LOCK="$SERVER_REPO/openapi/contract.lock.json"
[ -f "$CONTRACT_LOCK" ] || die_fail "openapi/contract.lock.json is missing from the server checkout"
LOCKED_SPEC_PATH="$(jq -r '.spec // "openapi/openapi.json"' "$CONTRACT_LOCK")"
LOCKED_SHA="$(jq -r '.sha256 // empty' "$CONTRACT_LOCK")"
[ -n "$LOCKED_SHA" ] || die_fail "openapi/contract.lock.json records no sha256"

ACTUAL_CONTRACT_SHA="$(git -C "$SERVER_REPO" show "$SERVER_COMMIT:$LOCKED_SPEC_PATH" 2>/dev/null | sha256sum | cut -d' ' -f1)"
[ -n "$ACTUAL_CONTRACT_SHA" ] || die_fail "cannot read $LOCKED_SPEC_PATH at $SERVER_COMMIT"
[ "$ACTUAL_CONTRACT_SHA" = "$LOCKED_SHA" ] \
    || die_fail "canonical contract at $SERVER_COMMIT is sha256 $ACTUAL_CONTRACT_SHA but openapi/contract.lock.json records $LOCKED_SHA"
ok "server contract matches its lock (sha256 $LOCKED_SHA)"

# --------------------------------------------------------------------------
# 4-5. Every property that is a function of the two checkouts' BYTES, delegated
#      to ci/verify-web-provenance.sh.
#
# WHY DELEGATED. Those proofs have to be exercised as negative controls against
# deliberately broken web checkouts — a hand-edited generated file, an injected
# one, a mismatched digest, a pin from a different repository. Doing that here
# would mean giving THIS script a way to be pointed at a web checkout somebody
# hands it, and the reason this script is worth anything is that it cannot be
# aimed anywhere: it derives the web commit from the lock and clones it itself.
# So the aiming stays here and the proofs move next door, where the fixture
# suite can call them on synthetic repositories directly. Neither half can be
# weakened without the other noticing.
#
# WHAT IT PROVES, in both schemas: the pinned mirror matches its own recorded
# digest; `sourceCommit` is a full SHA that resolves in this server repository;
# the canonical contract there is byte-identical to the one under test; the
# recorded spec version agrees with the contract lock.
#
# Schema 1 additionally requires `sourceCommit` to be an ANCESTOR of the commit
# under test, exactly as this script always did.
#
# Schema 2 replaces that ancestry requirement with content, and pays for it: the
# recorded `canonicalSpecSha256` must equal the canonical digest at BOTH commits,
# the contract lock at the pinned commit must name the same bytes, the metadata
# key set is closed, the source repository is checked, the transform pipeline
# version must be one this gate knows, the generator identity must match the web
# checkout's own pins, and every file under `generated/` must match a manifest
# recomputed here from the bytes on disk. Ancestry is still computed and is
# reported as ANCESTOR or CONTENT_EQUIVALENT_NON_ANCESTOR; the label never
# shortens the list of proofs.
# --------------------------------------------------------------------------
INNER="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/verify-web-provenance.sh"
[ -x "$INNER" ] || die_indeterminate "ci/verify-web-provenance.sh is missing or not executable"

# This script deliberately does not run under `set -e` (see the header): every check here reports
# its own failure with a specific message, and a bare non-zero exit would replace all of them with
# silence. So capture the delegate's status explicitly rather than toggling errexit around it.
"$INNER" --web "$WEB" --server-repo "$SERVER_REPO" --server-commit "$SERVER_COMMIT" \
    | tee "$WORKDIR/web-provenance.log"
INNER_RC="${PIPESTATUS[0]}"
case "$INNER_RC" in
    0) : ;;
    2) die_indeterminate "ci/verify-web-provenance.sh could not answer the question (exit 2)" ;;
    *) die_fail "ci/verify-web-provenance.sh rejected this pair (exit $INNER_RC) — see the output above" ;;
esac

RESULT_LINE="$(grep -m1 '^web-provenance: result ' "$WORKDIR/web-provenance.log")"
SCHEMA="$(printf '%s' "$RESULT_LINE" | sed -n 's/.*schema=\([^ ]*\).*/\1/p')"
ANCESTRY="$(printf '%s' "$RESULT_LINE" | sed -n 's/.*ancestry=\([^ ]*\).*/\1/p')"
[ -n "$SCHEMA" ] && [ -n "$ANCESTRY" ] \
    || die_indeterminate "ci/verify-web-provenance.sh reported no machine-readable result line"

VERSION_JSON="$WEB/src/lib/tesserafin-sdk/spec/version.json"
GENERATED_DIR="$WEB/src/lib/tesserafin-sdk/generated"
SOURCE_COMMIT="$(jq -r '.sourceCommit' "$VERSION_JSON")"
SPEC_SHA="$(jq -r '.specSha256' "$VERSION_JSON")"
BEHIND="$(git -C "$SERVER_REPO" rev-list --count "$SOURCE_COMMIT..$SERVER_COMMIT" 2>/dev/null || echo '?')"
ok "web provenance verified under schema $SCHEMA (ancestry: $ANCESTRY, $BEHIND commits apart)"

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
  provenance schema      $SCHEMA
  ancestry               $ANCESTRY
  server commit          $SERVER_COMMIT
  server contract sha256 $ACTUAL_CONTRACT_SHA
  web repository         $WEB_REPOSITORY
  web commit (locked)    $WEB_COMMIT
  web pin sourceCommit   $SOURCE_COMMIT ($BEHIND commits behind, contract byte-identical)
  web pinned spec sha256 $SPEC_SHA
  generated files        $GENERATED_COUNT
EOF
exit 0
