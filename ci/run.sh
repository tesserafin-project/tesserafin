#!/usr/bin/env bash
# Local CI — single entry point.
#
# Why this exists (issue #62): GitHub refuses to allocate a hosted runner for
# this PRIVATE repo BEFORE the first step — the all3f0r1 account's 2000 free
# Actions minutes/month are account-wide and exhausted (~2646 weighted minutes
# in July 2026, drained mostly by another repo), and the spending limit is $0,
# so the overage is refused rather than billed. No self-hosted runner is
# registered either (total_count: 0). Until a human raises the spending limit,
# makes the repo public, or registers a runner with the labels
# self-hosted,linux,x64,reefin, this script IS the mandatory merge gate.
#
# This script purges every bin/ and obj/ in the checkout ITSELF, before the
# first compilation, and refuses to build if that purge did not complete
# (#94). The caller has no prerequisite to remember and no way to skip it:
# there is no flag for that. See ci/lib/clean-artifacts.sh for the contract
# and docs/local-ci.md ("Porte de référence") for why it matters.
# This script takes no arguments.
#
# What it does: builds the tesserafin-ci image from Dockerfile.ci (repo root),
# then runs `dotnet build` + `dotnet test` for the full solution inside a
# container. The repository is bind-mounted (not copied) so the container
# always tests the CURRENT checkout on disk — whatever branch is out,
# uncommitted changes included — which is what makes this script a valid
# gate for any branch.
#
# PR115d: the test run below excludes anything tagged Category=Smoke - today that is
# tests/Tesserafin.MediaEncoding.Tests/Encoder/HlsSmokeTests.cs, a real ffmpeg/HLS synthesis test that is
# meaningfully heavier than the rest of this suite and was scoped as an OPTIONAL, non-blocking stage.
# Run it explicitly with ./ci/smoke.sh (also Docker-based, same image) - see that script's header for
# exactly what it proves and does not prove.
#
# Issue #36 / openapi: the OpenAPI contract drift check is part of the dotnet test stage below, not a
# separate step here - OpenApiContractTests (tests/Tesserafin.Server.Integration.Tests) fails this gate
# when openapi/openapi.json no longer matches what the server produces, and its failure message
# names the fix (./ci/openapi-generate.sh). Keeping it inside the suite means it reuses the server
# boot the suite already pays for, and leaves this script unchanged in structure and runtime.
# See docs/openapi-contract.md.
#
# Issue #174 / [C3]: the PROVIDER-AUTHENTICATION structural audit is likewise part of the dotnet test
# stage below rather than a separate step. ProviderAuthAuditTests (tests/Tesserafin.Providers.Tests)
# reads ci/provider-auth-inventory.json and the COMPILED Tesserafin.Providers.dll and fails this gate
# when a third-party provider credential is compiled in, when a provider's outbound host or string
# constant is undeclared, or when an inventory entry describes a code path that no longer exists.
# It uses no entropy or length threshold: two of the three credentials this project inherited were
# six and eight characters long, so it identifies a credential by WHERE it is used, not by what it
# looks like. Gitleaks is complementary and does not cover this — it reported the contaminated image
# as clean, because the values were .NET const strings in the UTF-16LE metadata heap.
# See docs/provider-auth-audit.md.
#
# Issue #93 / [A7]: the server<->web RELEASE PAIR is deliberately NOT checked here.
# ci/verify-release-pair.sh needs a published image, a tesserafin-web checkout and a
# real browser; requiring all three would make ordinary server-only development
# depend on a neighbouring web checkout. It is a separate, explicitly invoked
# gate — see the note printed in SUMMARY below and
# docs/container/A7-server-web-release-pair.md.
#
# Issue #153-LTV-R9: the Live TV hostile-control grader proves itself here too, host-side, after the
# container stage - ci/hostile-controls/prove-undeclared-reporting.py and
# ci/hostile-controls/prove-schema-lockdown.py. Both are unconditional and both are folded into the
# exit status. See the banner near the end of this script for what each one replays.
#
# Usage:
#   ./ci/run.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# shellcheck source-path=SCRIPTDIR
# shellcheck source=lib/clean-artifacts.sh
source "$REPO_ROOT/ci/lib/clean-artifacts.sh"

IMAGE_TAG="tesserafin-ci"
NUGET_VOLUME="tesserafin-nuget"
SOLUTION="Tesserafin.sln"

banner() {
    echo ""
    echo "======================================================================"
    echo "== $*"
    echo "======================================================================"
}

START_TS=$(date +%s)

banner "Building ${IMAGE_TAG} from Dockerfile.ci"
# Build against an empty context: the image needs no repo files baked in
# (the repo is bind-mounted below), so building with the repo root as
# context would just tar up the whole working tree for nothing.
BUILD_CTX="$(mktemp -d)"
trap 'rm -rf "$BUILD_CTX"' EXIT
docker build -f Dockerfile.ci -t "$IMAGE_TAG" "$BUILD_CTX"

banner "Purging every bin/ and obj/ before the first compilation"
# Deliberately OUTSIDE the `set +e` region below: a cleanup failure must abort
# the gate before anything is compiled, not be folded into $STATUS and
# reported alongside build output. `set -e` is still in force here, and
# ci_clean_artifacts asserts from the host that nothing survived, so a purge
# that reports success without emptying the bind mount is still fatal.
# This is why the gate no longer depends on the caller purging by hand: a
# warm obj/ makes MSBuild skip projects, and a skipped project skips its
# Roslyn analyzers.
ci_clean_artifacts "$REPO_ROOT" "$IMAGE_TAG"
echo "No bin/ or obj/ directory remains under $REPO_ROOT."

banner "dotnet build + dotnet test (${SOLUTION}, full suite) inside container"
set +e
docker run --rm \
    -v "$PWD":/repo \
    -v "${NUGET_VOLUME}":/nuget \
    -w /repo \
    "$IMAGE_TAG" \
    bash -c "
        set -euo pipefail
        echo '-- dotnet restore --'
        dotnet restore '${SOLUTION}'
        echo '-- dotnet build (fail fast on errors, non-incremental) --'
        # --no-incremental is the second belt here, not the first. The repo is bind-mounted, so
        # obj/ survives between runs and across host-side builds - including ./ci/openapi-generate.sh,
        # which every PR touching a serialized surface is required to run FIRST. With a warm obj/,
        # MSBuild considers a project up to date and skips it, and skipping a project skips its
        # Roslyn analyzers: the gate then prints PASS for a tree that fails from a cold build.
        # That is not hypothetical - PRs #46 and #45 were both merged on a PASS obtained this way,
        # and the resulting master failed CA1034 on the first clean checkout.
        # The artifact purge above is what actually guarantees the cold state (#94); this flag
        # keeps the build honest even if a stage between the purge and here writes into obj/.
        dotnet build '${SOLUTION}' --no-restore --no-incremental -clp:ErrorsOnly
        echo '-- dotnet test (full suite, excluding the optional PR115d smoke stage) --'
        dotnet test '${SOLUTION}' --no-build --nologo --filter 'Category!=Smoke'
    "
STATUS=$?
set -e

# The container runs as root (the dotnet SDK image default) and the repo is
# bind-mounted read-write, so bin/ and obj/ directories written back into the
# working tree end up root-owned. They're gitignored so this never affects
# `git status`/commits, but a root-owned obj/ WILL make the *next* host-side
# (or container-side) `dotnet build` fail with "Access denied" — so hand
# ownership of the working tree back to the invoking user regardless of
# whether the run above passed or failed. This is what makes ci/run.sh safe
# to run repeatedly against the same checkout.
banner "Restoring working tree ownership to $(id -u):$(id -g)"
docker run --rm -v "$PWD":/repo "$IMAGE_TAG" chown -R "$(id -u):$(id -g)" /repo || true

banner "Hostile-control grader self-proofs (#153-LTV-R7, #153-LTV-R9)"
# Issue #153-LTV-R9: these two probes are the ONLY thing that proves the Live TV hostile-control
# grader can still fail. Until R9 they existed in the tree and no permanent gate ran them, which is
# the same defect one level up as the one they exist to close: evidence nobody replays.
#
#   prove-undeclared-reporting.py  runs cc-10 twice against the same tree - once through run.py as
#                                  committed, once through a copy whose ONE mutated line drops the
#                                  undeclared failures from the classification - and requires the
#                                  second to FAIL while the grade stays RED. Remove the reporting
#                                  #153-LTV-R6 finding R6-1 added and this goes red.
#   prove-schema-lockdown.py       replays the eight situations of #153-LTV-R9 step 3: the R8 opt-out
#                                  at either value, `expectUndeclaredFailures` on a roster line, any
#                                  unknown key, that same list moved off cc-10, and cc-10's list
#                                  amputated / padded / perturbed. All eight must be refused.
#
# They run on the HOST, not in the container: run.py creates its own detached git worktree from HEAD
# and builds it with the host SDK, so a container would need the repository's git directory and a
# second SDK for nothing. They mutate nothing in this checkout - every build happens under a
# temporary worktree in $TMPDIR, which they remove - and they are graded against HEAD, so an
# uncommitted change to ci/hostile-controls is not what they measure.
#
# There is deliberately no flag, environment variable or condition that skips either one: a gate the
# caller can turn off is the finding, not the fix. Their exit status is folded into $STATUS below.
set +e
python3 "$REPO_ROOT/ci/hostile-controls/prove-undeclared-reporting.py"
PROBE_UNDECLARED_STATUS=$?
python3 "$REPO_ROOT/ci/hostile-controls/prove-schema-lockdown.py"
PROBE_SCHEMA_STATUS=$?
set -e

echo ""
echo "prove-undeclared-reporting.py exit ${PROBE_UNDECLARED_STATUS}"
echo "prove-schema-lockdown.py exit ${PROBE_SCHEMA_STATUS}"
if [ "$PROBE_UNDECLARED_STATUS" -ne 0 ] || [ "$PROBE_SCHEMA_STATUS" -ne 0 ]; then
    echo "The hostile-control grader did not prove itself; this gate FAILS." >&2
    STATUS=1
fi

banner "The W1 retention gate roster is invoked (#236, W1-A4-R2 finding D3)"
# The retention contract's gates are a closed roster behind one command in
# .github/workflows/w1-windows-runtime-retention.yml. Until W1-A4-R2 that
# invocation was "proved" by the retention subtree's own boundary.py testing
# whether the string "boundary.py" appeared anywhere in the workflow, which a
# COMMENT satisfies -- so the invocation could be commented out while every
# check still reported the gate as pinned.
#
# The pin therefore lives HERE, outside the subtree it pins, because a pin the
# pinned code performs disappears the moment that code is deleted. It parses the
# workflow as YAML and requires the exact job, the exact command as an active
# `run` value, no continue-on-error, no unreachable condition and no `|| true`.
# It reads only; it builds nothing and mutates nothing, and it is deliberately
# not skippable by flag or environment variable.
set +e
python3 "$REPO_ROOT/ci/windows/verify-retention-gate-pinned.py"
RETENTION_PIN_STATUS=$?
set -e
echo ""
echo "verify-retention-gate-pinned.py exit ${RETENTION_PIN_STATUS}"
if [ "$RETENTION_PIN_STATUS" -ne 0 ]; then
    echo "The W1 retention gate roster is not provably invoked; this gate FAILS." >&2
    STATUS=1
fi

END_TS=$(date +%s)
ELAPSED=$((END_TS - START_TS))

banner "SUMMARY"
if [ "$STATUS" -eq 0 ]; then
    echo "RESULT: PASS — build succeeded, full test suite green (${ELAPSED}s wall time)"
    echo ""
    echo "This gate covers the SERVER ONLY. It says nothing about whether a published"
    echo "image and a tesserafin-web commit are the same release. That is #93 / [A7]:"
    echo ""
    echo "  ci/verify-release-pair.sh --server-image <ref>@sha256:<digest> \\"
    echo "      --server-source <40-char commit> \\"
    echo "      --web-repo <path to tesserafin-web> --web-source <40-char commit>"
    echo ""
    echo "Run it explicitly before a release. Neither gate is hosted CI (#62, #94)."
else
    echo "RESULT: FAIL — see the first failing stage above (dotnet restore/build/test) (${ELAPSED}s wall time)" >&2
fi

exit "$STATUS"
