#!/usr/bin/env bash
# Deterministic negative controls for ci/verify-sdk-provenance.sh — the
# cross-repository SDK provenance gate of C4 (#97, #164).
#
# WHY THIS FILE EXISTS. A gate that has never been observed failing is not
# evidence that it can fail. Every control below mutates a throwaway lock, a
# throwaway server checkout or an argument, and requires the verifier to reject
# it FOR THE RIGHT REASON: a control that unexpectedly passes fails this suite,
# and so does one that fails with the wrong exit code or the wrong message.
#
# Controls 1-6 and 13 were previously inlined in
# .github/workflows/sdk-provenance.yml and are unchanged in behaviour. They live
# here so a maintainer can run the same controls locally, exactly as CI runs
# them, without a hosted runner. Controls 7-12 are new (#164).
#
# THE SERVER CHECKOUT IS NEVER MUTATED. Controls that need a different or a
# broken server tree get a scratch `git worktree`, removed again before this
# suite exits. The suite asserts at the end that the checkout it was invoked
# from is byte-identical to how it found it and that no scratch worktree
# survived.
#
# NOT COVERED HERE, stated rather than silently omitted. Five failure modes
# named in #164 live on the web side of the pair — missing pinned spec,
# mismatched `specSha256`, generated-SDK drift, generator failure, and the
# web verifier's announced `UNVERIFIED` degraded state. Each is implemented as a
# fail-closed path in ci/verify-sdk-provenance.sh, but exercising it from here
# would require giving the verifier a way to be pointed at a mutated web
# checkout instead of the repository its own lock names. That override is an
# escape hatch in a gate whose value is that it cannot be aimed anywhere else,
# so it is deliberately not added. Those five are property-1 territory, which
# #164 records as satisfied and self-referential by construction, and they are
# exercised by tesserafin-web's own gate.
#
# NETWORK. Controls whose rejection happens after the anonymous web clone really
# do clone tesserafin-web over HTTPS. That is part of the behaviour under test.
#
# Usage:
#   ./ci/tests/sdk-provenance.test.sh
#
#   SERVER_COMMIT=<40 hex>   commit to analyse (default: the checkout HEAD)
#   CONTROLS_DIR=<dir>       scratch directory (default: a fresh mktemp -d)
#
# Exit status: 0 every control behaved as required, 1 otherwise.

# NOT `set -e`: every control here is EXPECTED to exit non-zero, so `-e` would
# kill the suite at the first one before its status is ever read.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERIFIER="$REPO_ROOT/ci/verify-sdk-provenance.sh"
REAL_LOCK="$REPO_ROOT/ci/web-pair.lock.json"

for tool in git jq sha256sum; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "sdk-provenance controls: required tool '$tool' is not on PATH" >&2
        exit 1
    }
done
[ -x "$VERIFIER" ] || { echo "sdk-provenance controls: $VERIFIER is not executable" >&2; exit 1; }
[ -f "$REAL_LOCK" ] || { echo "sdk-provenance controls: $REAL_LOCK is missing" >&2; exit 1; }

SERVER_COMMIT="${SERVER_COMMIT:-$(git -C "$REPO_ROOT" rev-parse HEAD)}"
CONTROLS_DIR="${CONTROLS_DIR:-$(mktemp -d)}"
rm -rf "${CONTROLS_DIR:?}"; mkdir -p "$CONTROLS_DIR"

WEB_COMMIT="$(jq -r .webCommit "$REAL_LOCK")"

failures=0
SCRATCH_WORKTREES=()
WT_PATH=""

note()  { printf '%s\n' "$*"; }
error() { printf '::error::%s\n' "$*" >&2; }

# Detach a scratch worktree of the server repository at $1 and put its path in
# WT_PATH. A command substitution would run this in a subshell and lose the
# bookkeeping, so the result is returned through a global on purpose.
scratch_worktree() {
    local at="$1" name="$2"
    local path="$CONTROLS_DIR/wt-$name"
    WT_PATH=""
    if ! git -C "$REPO_ROOT" worktree add --quiet --detach "$path" "$at" >/dev/null 2>&1; then
        error "cannot create a scratch worktree at $at"
        return 1
    fi
    SCRATCH_WORKTREES+=("$path")
    WT_PATH="$path"
}

# Commit whatever is modified in a scratch worktree, with an identity of its own
# so this never depends on the runner's git configuration.
scratch_commit() {
    git -C "$1" -c user.name='SDK provenance controls' \
        -c user.email='controls@invalid' -c commit.gpgsign=false \
        commit --quiet --no-verify -a -m "$2" >/dev/null 2>&1
}

cleanup() {
    local wt
    for wt in ${SCRATCH_WORKTREES[@]+"${SCRATCH_WORKTREES[@]}"}; do
        git -C "$REPO_ROOT" worktree remove --force "$wt" >/dev/null 2>&1 || rm -rf "$wt"
    done
    SCRATCH_WORKTREES=()
    git -C "$REPO_ROOT" worktree prune >/dev/null 2>&1
    rm -rf "$CONTROLS_DIR"
}
trap cleanup EXIT

# run_control <name> <expected exit> <expected message fragment> [verifier args...]
run_control() {
    local name="$1" expected_rc="$2" fragment="$3"; shift 3
    local out rc
    out=$("$VERIFIER" --workdir "$CONTROLS_DIR/wd-$name" "$@" 2>&1)
    rc=$?
    rm -rf "$CONTROLS_DIR/wd-$name"
    if [ "$rc" -eq 0 ]; then
        error "negative control '$name' PASSED the gate; it must not"
        failures=$((failures + 1)); return
    fi
    if [ "$rc" -ne "$expected_rc" ]; then
        error "negative control '$name' exited $rc, expected $expected_rc"
        printf '%s\n' "$out" | tail -3 >&2
        failures=$((failures + 1)); return
    fi
    if ! printf '%s' "$out" | grep -qF "$fragment"; then
        error "negative control '$name' failed for the wrong reason (expected to mention: $fragment)"
        printf '%s\n' "$out" | tail -3 >&2
        failures=$((failures + 1)); return
    fi
    note "control '$name': rejected as expected (exit $rc)"
}

# --------------------------------------------------------------------------
# Group 1 — the pair lock. An abbreviated, mutable, absent or malformed pin is
# not an immutable pin, and a lock naming a repository nobody can read is an
# access failure, which must be red and never skipped.
# --------------------------------------------------------------------------

# 1. Abbreviated commit — an abbreviated pin is not immutable.
jq --arg c "${WEB_COMMIT:0:7}" '.webCommit = $c' "$REAL_LOCK" > "$CONTROLS_DIR/abbrev.json"
run_control abbreviated-commit 1 "is 7 characters" \
    --server-repo "$REPO_ROOT" --lock "$CONTROLS_DIR/abbrev.json" --server-commit "$SERVER_COMMIT"

# 2. A branch name where a commit SHA belongs.
jq '.webCommit = "main"' "$REAL_LOCK" > "$CONTROLS_DIR/branch.json"
run_control mutable-ref 1 "not lowercase hexadecimal" \
    --server-repo "$REPO_ROOT" --lock "$CONTROLS_DIR/branch.json" --server-commit "$SERVER_COMMIT"

# 3. Well-formed but nonexistent commit.
jq '.webCommit = "0000000000000000000000000000000000000000"' "$REAL_LOCK" > "$CONTROLS_DIR/ghost.json"
run_control nonexistent-commit 1 "does not exist in" \
    --server-repo "$REPO_ROOT" --lock "$CONTROLS_DIR/ghost.json" --server-commit "$SERVER_COMMIT"

# 4. Nonexistent repository — the access failure must be red, not skipped.
jq '.webRepository = "tesserafin-project/tesserafin-web-does-not-exist"' "$REAL_LOCK" \
    > "$CONTROLS_DIR/norepo.json"
run_control missing-repository 1 "cannot clone" \
    --server-repo "$REPO_ROOT" --lock "$CONTROLS_DIR/norepo.json" --server-commit "$SERVER_COMMIT"

# 5. Malformed lock.
printf 'not json at all\n' > "$CONTROLS_DIR/malformed.json"
run_control malformed-lock 1 "not valid JSON" \
    --server-repo "$REPO_ROOT" --lock "$CONTROLS_DIR/malformed.json" --server-commit "$SERVER_COMMIT"

# 6. Lock with no commit at all.
jq 'del(.webCommit)' "$REAL_LOCK" > "$CONTROLS_DIR/nocommit.json"
run_control lock-without-commit 1 "records no webCommit" \
    --server-repo "$REPO_ROOT" --lock "$CONTROLS_DIR/nocommit.json" --server-commit "$SERVER_COMMIT"

# --------------------------------------------------------------------------
# Group 2 — the server commit under test.
# --------------------------------------------------------------------------

# 7. Abbreviated server commit.
run_control abbreviated-server-commit 1 "is not a full 40-character SHA" \
    --server-repo "$REPO_ROOT" --lock "$REAL_LOCK" --server-commit "${SERVER_COMMIT:0:7}"

# 8. Well-formed but nonexistent server commit.
run_control nonexistent-server-commit 1 "does not exist in" \
    --server-repo "$REPO_ROOT" --lock "$REAL_LOCK" \
    --server-commit "0000000000000000000000000000000000000000"

# 9. Absent server checkout — the question was not answered (2), rather than a
#    property having failed (1).
run_control absent-server-checkout 2 "is not a git checkout" \
    --server-repo "$CONTROLS_DIR/there-is-no-checkout-here" --lock "$REAL_LOCK"

# --------------------------------------------------------------------------
# Group 3 — the contract. These need a server tree that differs from the one
# this suite was invoked from, so each gets a scratch worktree.
# --------------------------------------------------------------------------

# 10. Contract-lock digest mismatch: the analysed commit's canonical contract
#     does not hash to what openapi/contract.lock.json records.
if scratch_worktree "$SERVER_COMMIT" mismatch; then
    wt="$WT_PATH"
    jq '.sha256 = "0000000000000000000000000000000000000000000000000000000000000000"' \
        "$wt/openapi/contract.lock.json" > "$CONTROLS_DIR/mismatch.lock.json"
    cp "$CONTROLS_DIR/mismatch.lock.json" "$wt/openapi/contract.lock.json"
    run_control contract-lock-mismatch 1 "openapi/contract.lock.json records" \
        --server-repo "$wt" --lock "$REAL_LOCK" --server-commit "$SERVER_COMMIT"
else
    failures=$((failures + 1))
fi

# 11. The canonical contract MOVED after the web pin. A synthetic descendant of
#     the analysed commit whose contract really is different, with its lock
#     updated to match so that control 10's assertion is not what fires.
if scratch_worktree "$SERVER_COMMIT" moved; then
    wt="$WT_PATH"
    printf '\n' >> "$wt/openapi/openapi.json"
    moved_sha="$(sha256sum "$wt/openapi/openapi.json" | cut -d' ' -f1)"
    jq --arg s "$moved_sha" '.sha256 = $s' "$wt/openapi/contract.lock.json" \
        > "$CONTROLS_DIR/moved.lock.json"
    cp "$CONTROLS_DIR/moved.lock.json" "$wt/openapi/contract.lock.json"
    if scratch_commit "$wt" 'control: the canonical contract moved'; then
        run_control contract-moved-after-pin 1 "canonical contract MOVED after the web pin" \
            --server-repo "$wt" --lock "$REAL_LOCK" \
            --server-commit "$(git -C "$wt" rev-parse HEAD)"
    else
        error "control 'contract-moved-after-pin' could not commit its synthetic contract"
        failures=$((failures + 1))
    fi
else
    failures=$((failures + 1))
fi

# 12. A web sourceCommit that is NOT an ancestor of the commit under test.
#     Built without touching the web repository or the contract: a commit that
#     carries the analysed commit's TREE but hangs off the repository's root
#     commit, so nothing on master's history can be an ancestor of it. The tree
#     is identical, so neither the contract-lock assertion nor the version
#     assertion can fire first — only the ancestry one.
ROOT_COMMIT="$(git -C "$REPO_ROOT" rev-list --max-parents=0 "$SERVER_COMMIT" 2>/dev/null | tail -1)"
if [ -n "$ROOT_COMMIT" ] && scratch_worktree "$SERVER_COMMIT" nonancestral; then
    wt="$WT_PATH"
    nonanc_commit="$(GIT_AUTHOR_NAME='SDK provenance controls' \
        GIT_AUTHOR_EMAIL='controls@invalid' \
        GIT_COMMITTER_NAME='SDK provenance controls' \
        GIT_COMMITTER_EMAIL='controls@invalid' \
        git -C "$REPO_ROOT" commit-tree "$SERVER_COMMIT^{tree}" -p "$ROOT_COMMIT" \
            -m 'control: the analysed tree on a sibling history' 2>/dev/null)"
    if [ -n "$nonanc_commit" ]; then
        run_control non-ancestral-source-commit 1 "is NOT an ancestor of" \
            --server-repo "$wt" --lock "$REAL_LOCK" --server-commit "$nonanc_commit"
    else
        error "control 'non-ancestral-source-commit' could not craft its synthetic commit"
        failures=$((failures + 1))
    fi
else
    error "control 'non-ancestral-source-commit' could not resolve a root commit"
    failures=$((failures + 1))
fi

# --------------------------------------------------------------------------
# Group 4 — the verdict vocabulary. "The question was not answered" (2) must be
# distinguishable from "a property failed" (1).
# --------------------------------------------------------------------------

# 13. Missing tool. A completely empty PATH is the wrong control: the
#     `#!/usr/bin/env bash` shebang then cannot resolve `bash` and the shell
#     returns 127 before a single line of the script runs, which proves nothing
#     about the script. Build a PATH that satisfies every requirement EXCEPT jq.
PATH_BACKUP="$PATH"
mkdir -p "$CONTROLS_DIR/minbin"
for t in bash env git node npm sha256sum sed grep cut find head tail wc tr rm mkdir ls cat; do
    p=$(command -v "$t" 2>/dev/null)
    case "$p" in /*) ln -sf "$p" "$CONTROLS_DIR/minbin/$t" ;; esac
done
# `jq` is deliberately absent from minbin.
out=$(PATH="$CONTROLS_DIR/minbin" "$VERIFIER" \
        --server-repo "$REPO_ROOT" --workdir "$CONTROLS_DIR/wd-notool" 2>&1)
rc=$?
PATH="$PATH_BACKUP"
rm -rf "$CONTROLS_DIR/wd-notool"
if ! printf '%s' "$out" | grep -qF "required tool 'jq' is not on PATH"; then
    error "negative control 'missing-tool' did not report the missing tool"
    printf '%s\n' "$out" | tail -3 >&2
    failures=$((failures + 1))
fi
if [ "$rc" -ne 2 ]; then
    error "negative control 'missing-tool' exited $rc, expected 2 (INDETERMINATE)"
    failures=$((failures + 1))
else
    note "control 'missing-tool': INDETERMINATE as expected (exit 2)"
fi

# --------------------------------------------------------------------------
# The controls must leave nothing behind.
# --------------------------------------------------------------------------
cleanup
trap - EXIT

if ! git -C "$REPO_ROOT" diff --exit-code --quiet; then
    error "the negative controls left the working tree modified"
    git -C "$REPO_ROOT" status --porcelain >&2
    failures=$((failures + 1))
fi
if git -C "$REPO_ROOT" worktree list --porcelain | grep -qF "$CONTROLS_DIR"; then
    error "the negative controls left a scratch worktree registered"
    git -C "$REPO_ROOT" worktree list >&2
    failures=$((failures + 1))
fi

if [ "$failures" -ne 0 ]; then
    error "$failures negative control(s) did not behave as required"
    exit 1
fi
note "all negative controls behaved as required"
