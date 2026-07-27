#!/usr/bin/env bash
# Build-artifact purge — the fail-closed half of the local merge gate (#94).
#
# WHY THIS IS NOT OPTIONAL
# ------------------------
# ci/run.sh bind-mounts the working tree into the container, so bin/ and obj/
# survive between runs and across host-side builds. MSBuild treats a project
# with a warm obj/ as up to date and skips it, and skipping a project skips
# its Roslyn analyzers — the gate then prints PASS for a tree that fails from
# a cold checkout. That is not hypothetical: PRs #45 and #46 were both merged
# on a PASS obtained this way, and the resulting master failed CA1034 on the
# first clean build.
#
# `--no-incremental` alone does not close the hole: it forces a rebuild of the
# projects MSBuild decides to build, but stale artifacts on disk still change
# what the SDK sees. Until this commit the purge lived in documentation, as a
# command the caller was told to run FIRST. A gate whose soundness depends on
# the caller remembering a prerequisite is not a gate. This file owns it.
#
# CONTRACT
# --------
#   ci_clean_artifacts_find   <repo_root>              -> prints surviving dirs
#   ci_clean_artifacts_purge  <repo_root> <image_tag>  -> removes them, or fails
#   ci_clean_artifacts_assert <repo_root>              -> non-zero if any remain
#
# The purge runs INSIDE a container, as root, over the bind-mounted tree. That
# is deliberate: previous ci/run.sh invocations leave root-owned bin/ and obj/
# behind (the dotnet SDK image runs as root), and a host-side `rm -rf` cannot
# remove those without sudo. The container path removes user-owned and
# root-owned artifacts in one pass, with no privilege prompt on the host.
#
# Every failure here is fatal by design. There is no flag to skip the purge,
# no `|| true` on this path, and the assertion runs from the host afterwards
# so that a silently-failing purge cannot be mistaken for a clean tree.
#
# Standalone use (this is also how ci/tests/clean-artifacts.test.sh drives it):
#   ./ci/lib/clean-artifacts.sh <repo_root> <image_tag>

set -euo pipefail

# Print every build-artifact directory under <repo_root>, one per line.
# .git is pruned: it is not a build output and must never be touched. Matching
# directories are pruned too, so nested bin/obj inside a bin/ are reported once
# via their parent rather than as separate entries.
ci_clean_artifacts_find() {
    local repo_root="$1"
    find "$repo_root" \
        -path "$repo_root/.git" -prune -o \
        -type d \( -name bin -o -name obj \) -prune -print
}

# Remove every build-artifact directory under <repo_root>, including
# root-owned ones left by earlier container runs. Fails if docker is
# unavailable or the removal does not complete.
ci_clean_artifacts_purge() {
    local repo_root="$1"
    local image_tag="$2"

    docker run --rm \
        -v "$repo_root":/repo \
        -w /repo \
        "$image_tag" \
        bash -c '
            set -euo pipefail
            find /repo \
                -path /repo/.git -prune -o \
                -type d \( -name bin -o -name obj \) -prune -print0 \
            | xargs -0 --no-run-if-empty rm -rf --
        '
}

# Fail if any build-artifact directory survived the purge. Read from the HOST,
# not from inside the container, so that a purge which reported success while
# leaving the bind mount untouched is still caught.
ci_clean_artifacts_assert() {
    local repo_root="$1"
    local survivors
    survivors="$(ci_clean_artifacts_find "$repo_root")"

    if [ -n "$survivors" ]; then
        echo "FATAL: build artifacts survived the pre-build purge:" >&2
        echo "$survivors" >&2
        echo "" >&2
        echo "The gate refuses to build on a tree it could not clean: a stale obj/" >&2
        echo "makes MSBuild skip projects, and a skipped project skips its Roslyn" >&2
        echo "analyzers, which produces a PASS that a clean checkout would not." >&2
        return 1
    fi
}

# Purge then assert. This is the entry point ci/run.sh calls; keeping the two
# steps behind one name means no caller can perform the purge and forget the
# verification.
ci_clean_artifacts() {
    local repo_root="$1"
    local image_tag="$2"

    ci_clean_artifacts_purge "$repo_root" "$image_tag"
    ci_clean_artifacts_assert "$repo_root"
}

# Executed rather than sourced: run the full contract against the arguments.
if [ "${BASH_SOURCE[0]}" = "${0}" ]; then
    if [ "$#" -ne 2 ]; then
        echo "usage: ${0} <repo_root> <image_tag>" >&2
        exit 2
    fi
    ci_clean_artifacts "$1" "$2"
fi
