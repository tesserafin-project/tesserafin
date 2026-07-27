#!/usr/bin/env bash
# Tests for ci/lib/clean-artifacts.sh — the fail-closed purge that ci/run.sh
# performs before it compiles anything (#94).
#
# These run against a throwaway fixture tree under $TMPDIR, never against the
# repository checkout, so they are safe to run at any time. They do need
# docker: the whole point of the helper is that it removes ROOT-OWNED
# artifacts left behind by earlier container runs, and that case cannot be
# reproduced or verified without a container.
#
# Usage:
#   ./ci/tests/clean-artifacts.test.sh
#
# Override the image with CI_IMAGE=<tag>. The default is the same image the
# gate itself uses; it is built from Dockerfile.ci if it is not present.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
IMAGE_TAG="${CI_IMAGE:-tesserafin-ci}"

# shellcheck source-path=SCRIPTDIR
# shellcheck source=../lib/clean-artifacts.sh
source "$REPO_ROOT/ci/lib/clean-artifacts.sh"

PASS=0
FAIL=0

ok()   { echo "  PASS: $*"; PASS=$((PASS + 1)); }
bad()  { echo "  FAIL: $*" >&2; FAIL=$((FAIL + 1)); }
head() { echo ""; echo "== $*"; }

if ! docker image inspect "$IMAGE_TAG" >/dev/null 2>&1; then
    echo "-- building $IMAGE_TAG from Dockerfile.ci (empty context) --"
    _ctx="$(mktemp -d)"
    docker build -f "$REPO_ROOT/Dockerfile.ci" -t "$IMAGE_TAG" "$_ctx" >/dev/null
    rm -rf "$_ctx"
fi

FIXTURE="$(mktemp -d)"
cleanup() {
    # The fixture contains root-owned directories on purpose; hand them back
    # before removing, otherwise this trap cannot delete its own tree.
    docker run --rm -v "$FIXTURE":/fixture "$IMAGE_TAG" \
        chown -R "$(id -u):$(id -g)" /fixture >/dev/null 2>&1 || true
    rm -rf "$FIXTURE"
}
trap cleanup EXIT

# Rebuild the poisoned tree from scratch. Mirrors what a real checkout looks
# like after a host build plus a previous ci/run.sh: nested user-owned
# artifacts, a .git directory that must survive, and one root-owned obj/ that
# only the container path can remove.
seed_fixture() {
    # A previous case may have left root-owned leftovers that the host cannot
    # delete; hand them back before resetting. This is fixture bookkeeping,
    # not part of the behaviour under test.
    if [ -d "${FIXTURE:?}/tree" ]; then
        docker run --rm -v "$FIXTURE/tree":/repo "$IMAGE_TAG" \
            chown -R "$(id -u):$(id -g)" /repo >/dev/null
    fi
    rm -rf "${FIXTURE:?}/tree"
    mkdir -p "$FIXTURE/tree/.git/objects"
    echo "must survive" > "$FIXTURE/tree/.git/objects/keep"

    # Nested, user-owned.
    mkdir -p "$FIXTURE/tree/src/Project.A/bin/Debug/net10.0"
    mkdir -p "$FIXTURE/tree/src/Project.A/obj/Debug/net10.0"
    mkdir -p "$FIXTURE/tree/tests/Project.A.Tests/obj/Release"
    echo stale > "$FIXTURE/tree/src/Project.A/bin/Debug/net10.0/Project.A.dll"
    echo stale > "$FIXTURE/tree/src/Project.A/obj/Debug/net10.0/project.assets.json"
    echo stale > "$FIXTURE/tree/tests/Project.A.Tests/obj/Release/marker"

    # A source file that merely LIVES next to artifacts must not be touched.
    echo "namespace A;" > "$FIXTURE/tree/src/Project.A/Class.cs"

    # Root-owned, written by a previous container run. mkdir inside the
    # container rather than sudo on the host: no prompt, genuinely root-owned.
    docker run --rm -v "$FIXTURE/tree":/repo "$IMAGE_TAG" bash -c '
        set -euo pipefail
        mkdir -p /repo/src/Project.B/obj/Debug/net10.0
        echo stale > /repo/src/Project.B/obj/Debug/net10.0/project.assets.json
        chmod -R 700 /repo/src/Project.B/obj
    ' >/dev/null
}

count_artifacts() {
    ci_clean_artifacts_find "$FIXTURE/tree" | wc -l
}

# ---------------------------------------------------------------------------
head "1. the seeded tree really is poisoned, and really is root-owned"
seed_fixture
n="$(count_artifacts)"
[ "$n" -eq 4 ] && ok "4 artifact directories present" \
               || bad "expected 4 artifact directories, found $n"

owner="$(stat -c '%u' "$FIXTURE/tree/src/Project.B/obj")"
[ "$owner" = "0" ] && ok "src/Project.B/obj is root-owned (uid 0)" \
                   || bad "expected uid 0 on src/Project.B/obj, got $owner"

# ---------------------------------------------------------------------------
head "2. pre-fix behaviour: a host-side purge cannot clean this tree"
# This is what the old docs told the caller to run by hand. It fails on the
# root-owned directory, so a caller who ran it and did not read the exit
# status walks into the build with the poison still in place.
set +e
find "$FIXTURE/tree" -path "$FIXTURE/tree/.git" -prune -o \
     -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null
host_rc=$?
set -e
[ "$host_rc" -ne 0 ] && ok "host-side rm -rf failed (rc=$host_rc) as expected" \
                     || bad "host-side rm -rf unexpectedly succeeded"

n="$(count_artifacts)"
[ "$n" -eq 1 ] && ok "poison survives the host-side purge (1 directory left)" \
               || bad "expected 1 surviving directory, found $n"

set +e
ci_clean_artifacts_assert "$FIXTURE/tree" >/dev/null 2>&1
assert_rc=$?
set -e
[ "$assert_rc" -ne 0 ] && ok "assertion rejects the half-cleaned tree" \
                       || bad "assertion accepted a tree with surviving artifacts"

# ---------------------------------------------------------------------------
head "3. the fixed helper cleans the same tree with no host privileges"
seed_fixture
ci_clean_artifacts "$FIXTURE/tree" "$IMAGE_TAG"
n="$(count_artifacts)"
[ "$n" -eq 0 ] && ok "no artifact directory survives ci_clean_artifacts" \
               || bad "expected 0 artifact directories, found $n"

[ -f "$FIXTURE/tree/.git/objects/keep" ] && ok ".git was not touched" \
                                         || bad ".git content was destroyed"
[ -f "$FIXTURE/tree/src/Project.A/Class.cs" ] && ok "sources beside artifacts survived" \
                                              || bad "a source file was destroyed"

# ---------------------------------------------------------------------------
head "4. the helper is idempotent on an already-clean tree"
ci_clean_artifacts "$FIXTURE/tree" "$IMAGE_TAG"
ok "second consecutive purge succeeded on a clean tree"

# ---------------------------------------------------------------------------
head "5. negative control: break the purge and the gate must turn red"
# Substitute a purge that does nothing. If the assertion is load-bearing, the
# combined entry point must still fail. If this test ever passes silently,
# the gate has stopped being fail-closed.
seed_fixture
ci_clean_artifacts_purge() { :; }
set +e
ci_clean_artifacts "$FIXTURE/tree" "$IMAGE_TAG" >/dev/null 2>&1
broken_rc=$?
set -e
[ "$broken_rc" -ne 0 ] && ok "no-op purge is caught by the assertion (rc=$broken_rc)" \
                       || bad "no-op purge was NOT caught — the gate is not fail-closed"

# ---------------------------------------------------------------------------
echo ""
echo "======================================================================"
echo "== clean-artifacts: $PASS passed, $FAIL failed"
echo "======================================================================"
[ "$FAIL" -eq 0 ]
