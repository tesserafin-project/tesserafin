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
# IMPORTANT: purge every bin/ and obj/ before running this as a merge gate:
#   find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
# See docs/local-ci.md ("Porte de référence") for why. This script takes no
# arguments.
#
# What it does: builds the reefin-ci image from Dockerfile.ci (repo root),
# then runs `dotnet build` + `dotnet test` for the full solution inside a
# container. The repository is bind-mounted (not copied) so the container
# always tests the CURRENT checkout on disk — whatever branch is out,
# uncommitted changes included — which is what makes this script a valid
# gate for any branch.
#
# PR115d: the test run below excludes anything tagged Category=Smoke - today that is
# tests/Reefin.MediaEncoding.Tests/Encoder/HlsSmokeTests.cs, a real ffmpeg/HLS synthesis test that is
# meaningfully heavier than the rest of this suite and was scoped as an OPTIONAL, non-blocking stage.
# Run it explicitly with ./ci/smoke.sh (also Docker-based, same image) - see that script's header for
# exactly what it proves and does not prove.
#
# Issue #36 / openapi: the OpenAPI contract drift check is part of the dotnet test stage below, not a
# separate step here - OpenApiContractTests (tests/Reefin.Server.Integration.Tests) fails this gate
# when openapi/openapi.json no longer matches what the server produces, and its failure message
# names the fix (./ci/openapi-generate.sh). Keeping it inside the suite means it reuses the server
# boot the suite already pays for, and leaves this script unchanged in structure and runtime.
# See docs/openapi-contract.md.
#
# Usage:
#   ./ci/run.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

IMAGE_TAG="reefin-ci"
NUGET_VOLUME="reefin-nuget"
SOLUTION="Reefin.sln"

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
        # --no-incremental is what makes this script an honest gate. The repo is bind-mounted, so
        # obj/ survives between runs and across host-side builds - including ./ci/openapi-generate.sh,
        # which every PR touching a serialized surface is required to run FIRST. With a warm obj/,
        # MSBuild considers a project up to date and skips it, and skipping a project skips its
        # Roslyn analyzers: the gate then prints PASS for a tree that fails from a cold build.
        # That is not hypothetical - PRs #46 and #45 were both merged on a PASS obtained this way,
        # and the resulting master failed CA1034 on the first clean checkout.
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
else
    echo "RESULT: FAIL — see the first failing stage above (dotnet restore/build/test) (${ELAPSED}s wall time)" >&2
fi

exit "$STATUS"
