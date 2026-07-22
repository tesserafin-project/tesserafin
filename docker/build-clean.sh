#!/usr/bin/env bash
# Clean, deterministic build of the Tesserafin production image (#87 / [A1]).
#
# Derives every build-affecting value from git + the canonical version source so
# the same commit always produces the same inputs, then drives docker-bake.hcl.
# No dependency on hosted GitHub Actions.
#
# Usage:
#   docker/build-clean.sh [--target server|amd64|arm64] [--output MODE] [--builder NAME]
#
#   --output load          load a single-arch image into the local docker (default)
#   --output oci:PATH      write a reproducible OCI layout tarball to PATH
#   --output push          push the multi-arch image to $REGISTRY
#
# Reproducibility: provenance and SBOM attestations are disabled (they embed
# wall-clock timestamps); layer/file mtimes are clamped to the commit time.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO_ROOT}"

TARGET="amd64"
OUTPUT="load"
BUILDER="${BUILDX_BUILDER:-tf-builder}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --target)  TARGET="$2"; shift 2 ;;
    --output)  OUTPUT="$2"; shift 2 ;;
    --builder) BUILDER="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

# --- Derive deterministic inputs -------------------------------------------
VERSION="$(grep -oP 'AssemblyVersion\("\K[0-9]+\.[0-9]+\.[0-9]+' SharedVersion.cs | head -1)"
[[ -n "${VERSION}" ]] || { echo "could not read VERSION from SharedVersion.cs" >&2; exit 1; }
VCS_REF="$(git rev-parse HEAD)"
SOURCE_DATE_EPOCH="$(git log -1 --format=%ct HEAD)"
BUILD_DATE="$(date -u -d "@${SOURCE_DATE_EPOCH}" +%Y-%m-%dT%H:%M:%SZ)"
REGISTRY="${REGISTRY:-ghcr.io/tesserafin-project/tesserafin}"
export VERSION VCS_REF SOURCE_DATE_EPOCH BUILD_DATE REGISTRY

if [[ -n "$(git status --porcelain)" ]]; then
  echo "WARNING: working tree is dirty — build is not from a clean commit." >&2
fi

echo "== Tesserafin production image build =="
echo "  version           : ${VERSION}"
echo "  commit            : ${VCS_REF}"
echo "  source_date_epoch : ${SOURCE_DATE_EPOCH} (${BUILD_DATE})"
echo "  target            : ${TARGET}"
echo "  output            : ${OUTPUT}"
echo "  builder           : ${BUILDER}"
echo "  registry          : ${REGISTRY}"

COMMON=( bake --builder "${BUILDER}" --no-cache
         --set "*.attest=type=provenance,disabled=true"
         --set "*.attest=type=sbom,disabled=true" )

case "${OUTPUT}" in
  load)
    exec docker buildx "${COMMON[@]}" --load "${TARGET}"
    ;;
  oci:*)
    DEST="${OUTPUT#oci:}"
    DESTDIR="$(cd "$(dirname "${DEST}")" && pwd)"
    # The docker-container builder needs an explicit fs.write entitlement to
    # export an OCI tarball outside the build context.
    exec docker buildx "${COMMON[@]}" \
        --allow "fs.write=${DESTDIR}" \
        --set "${TARGET}.output=type=oci,dest=${DEST},rewrite-timestamp=true" \
        "${TARGET}"
    ;;
  push)
    exec docker buildx "${COMMON[@]}" --push "${TARGET}"
    ;;
  *)
    echo "unknown --output: ${OUTPUT}" >&2; exit 2 ;;
esac
