#!/usr/bin/env bash
# Build the Tesserafin FFmpeg runtime for one architecture (F0 / #229).
#
# Usage: ci/ffmpeg/build-runtime.sh --arch linux-x64|linux-arm64 --out DIR
#
# Fetches the pinned sources on the host, then hands them to the digest-pinned
# builder image. The container gets the source cache and an output directory and
# nothing else: no network, no host toolchain, no workstation state. Publishing
# is not possible from here — there is no registry, no token and no push.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ARCH=""; OUT=""; CACHE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch)  ARCH="$2"; shift 2 ;;
        --out)   OUT="$2"; shift 2 ;;
        --cache) CACHE="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -n "${ARCH}" ]] || ff_die "--arch is required"
[[ -n "${OUT}" ]]  || ff_die "--out is required"
CACHE="${CACHE:-${OUT}/source-cache}"

ff_load_manifest
TRIPLET="$(ff_arch_triplet "${ARCH}")"
[[ "$(uname -m)" == "${TRIPLET}" ]] || ff_die \
    "this host is $(uname -m), ${ARCH} needs ${TRIPLET}. The runtime is never cross-built or emulated."

mkdir -p "${OUT}" "${CACHE}"

ff_log "policy gate"
"${FF_REPO_ROOT}/ci/ffmpeg/verify-components.sh" >&2

ff_log "fetching pinned sources into ${CACHE}"
"${FF_REPO_ROOT}/ci/ffmpeg/fetch-sources.sh" --cache "${CACHE}"

ff_log "materialising the builder image from ${FF_BUILDER_IMAGE}"
# Built here rather than pulled: this project publishes nothing, so there is no
# registry to pull a builder from. The Dockerfile pins the base by digest and
# every package to one snapshot.debian.org timestamp, so building it twice
# installs the same bytes.
BUILDER_TAG="tesserafin-ffmpeg-builder:${FF_BUILD_REVISION}"
docker build --quiet --tag "${BUILDER_TAG}" "${FF_REPO_ROOT}/ci/ffmpeg/builder" >/dev/null
docker run --rm "${BUILDER_TAG}" cat /toolchain.txt >&2

ff_log "building ${ARCH}"
# --network none: every byte the build consumes was fetched and digest-checked
# above, so the build itself has nothing left to download. A recipe that tries
# fails loudly instead of silently introducing an unpinned input.
docker run --rm \
    --network none \
    --user "$(id -u):$(id -g)" \
    --env HOME=/tmp \
    --volume "${FF_REPO_ROOT}/ci/ffmpeg:/ci:ro" \
    --volume "${CACHE}:/cache:ro" \
    --volume "${OUT}:/out" \
    --workdir /tmp \
    "${BUILDER_TAG}" \
    bash -eo pipefail -c '
        export DEBIAN_FRONTEND=noninteractive
        /ci/build-in-container.sh --cache /cache --out /out --arch '"${ARCH}"'
    '

ff_log "runtime staged under ${OUT}"
