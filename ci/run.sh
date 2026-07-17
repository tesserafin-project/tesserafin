#!/usr/bin/env bash
# Local CI — single entry point.
#
# Why this exists: hosted GitHub Actions quota has been exhausted since
# ~2026-07-06. Until it resets (or a self-hosted runner picks up
# .github/workflows/local-ci.yml), this script IS the mandatory merge gate.
# See docs/local-ci.md.
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
        echo '-- dotnet build (fail fast on errors) --'
        dotnet build '${SOLUTION}' --no-restore -clp:ErrorsOnly
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
