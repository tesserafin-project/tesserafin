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
# Issue #93 / [A7]: the server<->web RELEASE PAIR is deliberately NOT checked here.
# ci/verify-release-pair.sh needs a published image, a tesserafin-web checkout and a
# real browser; requiring all three would make ordinary server-only development
# depend on a neighbouring web checkout. It is a separate, explicitly invoked
# gate — see the note printed in SUMMARY below and
# docs/container/A7-server-web-release-pair.md.
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

banner "Namespace guard (#147)"
# Deliberately FIRST and outside the `set +e` region below: it needs no image,
# no container and no compilation, it runs in well under a second, and a tree
# that has reintroduced the old GitHub organisation must not reach the point
# where a green build would report PASS. `set -e` is in force, so a violation
# aborts the gate here.
./ci/verify-namespace.sh

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
