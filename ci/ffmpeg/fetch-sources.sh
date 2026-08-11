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

# Idempotent by construction: a cache left behind by an interrupted run is a
# normal state, not a reason to trust whatever is on disk. The only thing that
# satisfies this function is HEAD equal to the pinned commit.
ff_git_at_commit() { # <dir> <repo> <commit> <fetch-submodules>
    local dir="$1" repo="$2" commit="$3" submodules="$4"
    if [[ "$(git -C "${dir}" rev-parse HEAD 2>/dev/null || true)" != "${commit}" ]]; then
        rm -rf "${dir}"
        git init -q "${dir}"
        git -C "${dir}" remote add origin "${repo}"
        git -C "${dir}" fetch -q --depth=1 origin "${commit}"
        git -C "${dir}" checkout -q FETCH_HEAD
    fi
    local got
    got="$(git -C "${dir}" rev-parse HEAD)"
    [[ "${got}" == "${commit}" ]] \
        || ff_die "commit mismatch in ${dir}: got ${got}, the manifest pins ${commit}"
    # Submodules are fetched HERE, not in the build container, which has no
    # network. They need no separate pin: the parent tree's gitlink already
    # names an exact commit for each one.
    if [[ "${submodules}" == "True" && -f "${dir}/.gitmodules" ]]; then
        git -C "${dir}" submodule update --init --recursive --depth=1 --quiet
    fi
}

# --- the FFmpeg baseline ------------------------------------------------------
ff_log "fetching jellyfin-ffmpeg ${FF_FFMPEG_BASELINE} @ ${FF_FFMPEG_COMMIT}"
ff_git_at_commit "${CACHE}/git/jellyfin-ffmpeg" "${FF_FFMPEG_REPO}" "${FF_FFMPEG_COMMIT}" False
ff_log "ffmpeg source at ${FF_FFMPEG_COMMIT}"

# --- every component ----------------------------------------------------------
while IFS=$'\t' read -r name kind pin src submodules mirrors; do
    [[ -n "${name}" ]] || continue
    case "${kind}" in
        tar)
            dest="${CACHE}/archives/${name}-$(basename "${src}")"
            if [[ ! -f "${dest}" ]]; then
                ff_log "fetching ${name}"
                # --retry-all-errors, not just --retry. curl's plain --retry
                # covers timeouts, 5xx and 429 only, so an HTTP status outside
                # that set kills the fetch on the first attempt with no retry at
                # all. www.freedesktop.org answered a hosted x64 runner with 418
                # while the arm64 runner in the same run fetched the identical
                # URL successfully, and `--retry 3` returned exit 22 instantly.
                # A shared-IP CI runner meeting a rate limiter is a permanent
                # condition of hosted builds, not a freak event.
                #
                # This changes nothing about what may be fetched: the URL is
                # still the pinned one and the SHA-256 below is still checked
                # against the manifest, so a retry can only ever obtain the same
                # bytes or fail.
                # The pinned URL first, then any declared mirror. A mirror is
                # not a second pin: the SHA-256 check below is unchanged and
                # applies to whichever host answered, so a mirror can only ever
                # supply the same bytes or fail the build.
                #
                # Needed because retrying is not always enough. A hosted run
                # retried a 502 from download.savannah.gnu.org six times over
                # 34 s and still could not fetch freetype. Every clean build
                # fetches 20 tarballs from 12 distinct hosts, and a run performs
                # four such fetches, so single-homed sources fail regularly.
                fetched=0
                for candidate in "${src}" ${mirrors//,/ }; do
                    if curl --fail --silent --show-error --location \
                            --retry 5 --retry-all-errors --retry-max-time 180 \
                            --connect-timeout 30 \
                            --output "${dest}.part" "${candidate}"; then
                        fetched=1
                        [[ "${candidate}" == "${src}" ]] || ff_log "  (from mirror ${candidate})"
                        break
                    fi
                    ff_log "  ${candidate} did not answer; trying the next source"
                done
                [[ "${fetched}" -eq 1 ]] || ff_die "every source for ${name} failed"
                mv "${dest}.part" "${dest}"
            fi
            got="$(ff_sha256 "${dest}")"
            [[ "${got}" == "${pin}" ]] \
                || ff_die "checksum mismatch for ${name}: got ${got}, the manifest pins ${pin}"
            ;;
        git)
            ff_log "cloning ${name} @ ${pin}"
            ff_git_at_commit "${CACHE}/git/${name}" "${src}" "${pin}" "${submodules}"
            ;;
        *) ff_die "${name} has unknown sourceType '${kind}'" ;;
    esac
done < <(python3 - "${FF_COMPONENTS}" <<'PY'
import json, sys
for c in json.load(open(sys.argv[1]))["components"]:
    print("\t".join([c["name"], c["sourceType"],
                     c.get("sha256") or c["commit"],
                     c.get("url") or c["repository"],
                     str(c.get("submodules", False)),
                     ",".join(c.get("mirrors", []))]))
PY
)

ff_log "every pinned source is present and matches its digest"
