#!/usr/bin/env bash
# Fetch every pinned source into a cache that IS the corresponding-source
# artifact (F0 / #229).
#
# Usage: ci/ffmpeg/fetch-sources.sh --cache DIR
#
# Two properties this exists to hold:
#
#   1. nothing is built from bytes whose digest was not checked first. A
#      checksum mismatch stops the build; it is never a warning.
#   2. the fetched trees are RETAINED. GPLv3 §1 asks for the preferred form for
#      modification, so the build cannot fetch-and-discard and then reconstruct
#      a source artifact from links afterwards — the cache is the artifact.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

CACHE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --cache) CACHE="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -n "${CACHE}" ]] || ff_die "--cache is required"

ff_load_manifest
mkdir -p "${CACHE}/archives" "${CACHE}/git"

# --- the FFmpeg baseline ------------------------------------------------------
FFMPEG_DIR="${CACHE}/git/jellyfin-ffmpeg"
if [[ ! -d "${FFMPEG_DIR}/.git" ]]; then
    ff_log "fetching jellyfin-ffmpeg ${FF_FFMPEG_BASELINE} @ ${FF_FFMPEG_COMMIT}"
    git init -q "${FFMPEG_DIR}"
    git -C "${FFMPEG_DIR}" remote add origin "${FF_FFMPEG_REPO}"
    git -C "${FFMPEG_DIR}" fetch -q --depth=1 origin "${FF_FFMPEG_COMMIT}"
    git -C "${FFMPEG_DIR}" checkout -q FETCH_HEAD
fi
actual="$(git -C "${FFMPEG_DIR}" rev-parse HEAD)"
[[ "${actual}" == "${FF_FFMPEG_COMMIT}" ]] \
    || ff_die "ffmpeg checkout is ${actual}, the manifest pins ${FF_FFMPEG_COMMIT}"
ff_log "ffmpeg source at ${actual}"

# --- every component ----------------------------------------------------------
while IFS=$'\t' read -r name kind pin src; do
    [[ -n "${name}" ]] || continue
    case "${kind}" in
        tar)
            dest="${CACHE}/archives/${name}-$(basename "${src}")"
            if [[ ! -f "${dest}" ]]; then
                ff_log "fetching ${name}"
                curl --fail --silent --show-error --location --retry 3 \
                     --output "${dest}.part" "${src}"
                mv "${dest}.part" "${dest}"
            fi
            got="$(ff_sha256 "${dest}")"
            [[ "${got}" == "${pin}" ]] \
                || ff_die "checksum mismatch for ${name}: got ${got}, the manifest pins ${pin}"
            ;;
        git)
            dir="${CACHE}/git/${name}"
            if [[ ! -d "${dir}/.git" ]]; then
                ff_log "cloning ${name} @ ${pin}"
                git init -q "${dir}"
                git -C "${dir}" remote add origin "${src}"
                git -C "${dir}" fetch -q --depth=1 origin "${pin}"
                git -C "${dir}" checkout -q FETCH_HEAD
            fi
            got="$(git -C "${dir}" rev-parse HEAD)"
            [[ "${got}" == "${pin}" ]] \
                || ff_die "commit mismatch for ${name}: got ${got}, the manifest pins ${pin}"
            ;;
        *) ff_die "${name} has unknown sourceType '${kind}'" ;;
    esac
done < <(python3 - "${FF_COMPONENTS}" <<'PY'
import json, sys
for c in json.load(open(sys.argv[1]))["components"]:
    print("\t".join([c["name"], c["sourceType"],
                     c.get("sha256") or c["commit"],
                     c.get("url") or c["repository"]]))
PY
)

ff_log "every pinned source is present and matches its digest"
