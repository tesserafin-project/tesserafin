#!/usr/bin/env bash
# Clean, deterministic build of the Tesserafin production image (#87 / [A1]).
#
# Every build-affecting value — version, tags, commit, timestamps — comes from
# docker/version-contract.sh (#92 / [A6]), never from logic duplicated here or in
# docker-bake.hcl. The same commit therefore always produces the same inputs.
# No dependency on hosted GitHub Actions.
#
# Usage:
#   docker/build-clean.sh [--target server|amd64|arm64] [--output MODE] [--builder NAME]
#                         [--channel dev|prerelease|stable] [--release-tag TAG] [--allow-dirty]
#
#   --output load          load a single-arch image into the local docker (default)
#   --output oci:PATH      write a reproducible OCI layout tarball to PATH
#   --output push          push the multi-arch image to $REGISTRY
#
# The default channel is `dev`, which only ever produces the two immutable tags
# `<version>-dev.<short commit>` and `sha-<commit>`. Moving channels (`preview`,
# `latest`, major/minor) are reachable only through an explicit --release-tag on
# the prerelease/stable channels; the contract refuses every other route.
#
# Reproducibility: provenance and SBOM attestations are disabled (they embed
# wall-clock timestamps); layer/file mtimes are clamped to the commit time.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO_ROOT}"

TARGET="amd64"
OUTPUT="load"
BUILDER="${BUILDX_BUILDER:-tf-builder}"
CONTRACT_ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --target)  TARGET="$2"; shift 2 ;;
    --output)  OUTPUT="$2"; shift 2 ;;
    --builder) BUILDER="$2"; shift 2 ;;
    --channel|--release-tag|--registry) CONTRACT_ARGS+=("$1" "$2"); shift 2 ;;
    --allow-dirty) CONTRACT_ARGS+=("$1"); shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

# --- Derive deterministic inputs from the version contract -------------------
# A contract violation (malformed version, tag/source mismatch, dirty release,
# missing provenance) exits non-zero here and no image is built.
CONTRACT_ENV="$(docker/version-contract.sh env "${CONTRACT_ARGS[@]+"${CONTRACT_ARGS[@]}"}")"
while IFS='=' read -r key value; do
  [[ -n "${key}" ]] || continue
  printf -v "${key}" '%s' "${value}"
  export "${key?}"
done <<<"${CONTRACT_ENV}"

if [[ "${CHANNEL}" == "dev" && -n "$(git status --porcelain)" ]]; then
  echo "WARNING: working tree is dirty — this dev build is not from a clean commit." >&2
fi

echo "== Tesserafin production image build =="
echo "  version           : ${VERSION}"
echo "  commit            : ${VCS_REF}"
echo "  source_date_epoch : ${SOURCE_DATE_EPOCH} (${BUILD_DATE})"
echo "  channel           : ${CHANNEL}"
echo "  tags              : ${TAGS}"
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
