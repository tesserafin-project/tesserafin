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
#
# The cache directory is UNTRUSTED INPUT (#232). It may have been restored by
# actions/cache, left behind by an interrupted run, or written by anything with
# access to the workspace. Every path below therefore ends at the same place: a
# byte is consumed only after it satisfies the pin in components.json, whether
# it was downloaded a second ago or restored from a cache saved weeks earlier.
# A cache hit is never a reason to trust anything.

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

# --- the inventory the manifest allows ----------------------------------------
# Read once, then used twice: to decide what may EXIST in the cache before
# anything is fetched, and to fetch it. Deriving both from the same rows is what
# makes "an additional component" and "a missing component" the same check.
mapfile -t FF_ROWS < <(python3 - "${FF_COMPONENTS}" <<'PY'
import json, sys
for c in json.load(open(sys.argv[1]))["components"]:
    print("\t".join([c["name"], c["sourceType"],
                     c.get("sha256") or c["commit"],
                     c.get("url") or c["repository"],
                     str(c.get("submodules", False)),
                     ",".join(c.get("mirrors", []))]))
PY
)
[[ "${#FF_ROWS[@]}" -gt 0 ]] || ff_die "${FF_COMPONENTS} declares no components"

declare -A FF_EXPECT_ARCHIVE=()
declare -A FF_EXPECT_GIT=()
# The FFmpeg baseline is not a component row, but it is a legitimate cache entry.
FF_EXPECT_GIT["jellyfin-ffmpeg"]=1
for row in "${FF_ROWS[@]}"; do
    IFS=$'\t' read -r _name _kind _pin _src _submodules _mirrors <<<"${row}"
    case "${_kind}" in
        tar) FF_EXPECT_ARCHIVE["${_name}-$(basename "${_src}")"]=1 ;;
        git) FF_EXPECT_GIT["${_name}"]=1 ;;
        *)   ff_die "${_name} has unknown sourceType '${_kind}'" ;;
    esac
done

# --- cache format ------------------------------------------------------------
# The on-disk layout a restored cache is allowed to have. A cache written by an
# older layout is discarded rather than reasoned about: it costs one download
# and removes a whole class of "the restore was subtly the wrong shape" bug.
#
# The stamp lives at the cache ROOT. package-runtime.sh reads only
# ${CACHE}/archives and ${CACHE}/git, so nothing here can reach a delivered
# artifact.
FF_SOURCE_CACHE_FORMAT=1
stamp="${CACHE}/.source-cache-format"
if [[ -d "${CACHE}/archives" || -d "${CACHE}/git" ]]; then
    if [[ "$(cat "${stamp}" 2>/dev/null || true)" != "${FF_SOURCE_CACHE_FORMAT}" ]]; then
        ff_log "the restored source cache has no recognised format stamp; discarding it"
        rm -rf "${CACHE}/archives" "${CACHE}/git"
    fi
fi
mkdir -p "${CACHE}/archives" "${CACHE}/git"
printf '%s\n' "${FF_SOURCE_CACHE_FORMAT}" > "${stamp}"

# --- reject anything the manifest does not name -------------------------------
# package-runtime.sh copies ${CACHE}/archives/* verbatim into the
# corresponding-source archive and walks ${CACHE}/git/*, so an unexpected entry
# here is not a stray file: it is a delivered byte. This runs BEFORE any digest
# is checked, because a component that is not in the manifest has no pin that
# could ever clear it. A half-downloaded '<name>.part' from a cancelled job is
# removed by the same rule.
ff_prune_unexpected() {
    local entry base
    for entry in "${CACHE}/archives"/* "${CACHE}/git"/*; do
        [[ -e "${entry}" ]] || continue
        base="$(basename "${entry}")"
        if [[ "${entry}" == "${CACHE}/archives/"* ]]; then
            if [[ -n "${FF_EXPECT_ARCHIVE[${base}]:-}" ]]; then
                continue
            fi
        elif [[ -n "${FF_EXPECT_GIT[${base}]:-}" ]]; then
            continue
        fi
        ff_log "discarding an unexpected source-cache entry: ${entry#"${CACHE}/"}"
        rm -rf "${entry}"
    done
}
ff_prune_unexpected

# Idempotent by construction: a cache left behind by an interrupted run, or
# restored from an earlier one, is a normal state and not a reason to trust
# whatever is on disk.
#
# HEAD alone is not enough. ff_deterministic_tar excludes .git, so the WORKING
# TREE is what ships as corresponding source: a restored tree sitting at the
# pinned commit with one altered or one added file would be delivered without
# any digest ever disagreeing. The origin, the commit, the working tree and the
# submodule state are therefore all checked, and anything short of all four
# means the tree is discarded and downloaded again from the pin.
ff_git_usable() { # <dir> <repo> <commit> <fetch-submodules>
    local dir="$1" repo="$2" commit="$3" submodules="$4"
    [[ -d "${dir}/.git" ]] || return 1
    [[ "$(git -C "${dir}" remote get-url origin 2>/dev/null || true)" == "${repo}" ]] || return 1
    [[ "$(git -C "${dir}" rev-parse HEAD 2>/dev/null || true)" == "${commit}" ]] || return 1
    [[ -z "$(git -C "${dir}" status --porcelain --untracked-files=all 2>/dev/null)" ]] || return 1
    if [[ "${submodules}" == "True" && -f "${dir}/.gitmodules" ]]; then
        # '-' is an uninitialised submodule, '+' one checked out at a commit the
        # parent tree does not record. Both mean these are not the pinned bytes.
        if git -C "${dir}" submodule status --recursive 2>/dev/null | grep -q '^[-+]'; then
            return 1
        fi
    fi
    return 0
}

ff_git_at_commit() { # <dir> <repo> <commit> <fetch-submodules>
    local dir="$1" repo="$2" commit="$3" submodules="$4"
    if ! ff_git_usable "${dir}" "${repo}" "${commit}" "${submodules}"; then
        if [[ -e "${dir}" ]]; then
            ff_log "  ${dir##*/}: the cached tree is not the pinned one; discarding it and cloning again"
        fi
        rm -rf "${dir}"
        git init -q "${dir}"
        git -C "${dir}" remote add origin "${repo}"
        git -C "${dir}" fetch -q --depth=1 origin "${commit}"
        git -C "${dir}" checkout -q FETCH_HEAD
        # Submodules are fetched HERE, not in the build container, which has no
        # network. They need no separate pin: the parent tree's gitlink already
        # names an exact commit for each one.
        if [[ "${submodules}" == "True" && -f "${dir}/.gitmodules" ]]; then
            git -C "${dir}" submodule update --init --recursive --depth=1 --quiet
        fi
    fi

    # Asserted unconditionally, on the restored path and the freshly cloned one
    # alike, and reported per property so a failure names what was wrong.
    local got origin dirty
    origin="$(git -C "${dir}" remote get-url origin)"
    [[ "${origin}" == "${repo}" ]] \
        || ff_die "origin mismatch in ${dir}: got ${origin}, the manifest pins ${repo}"
    got="$(git -C "${dir}" rev-parse HEAD)"
    [[ "${got}" == "${commit}" ]] \
        || ff_die "commit mismatch in ${dir}: got ${got}, the manifest pins ${commit}"
    dirty="$(git -C "${dir}" status --porcelain --untracked-files=all)"
    [[ -z "${dirty}" ]] \
        || ff_die "the tree in ${dir} does not match its pinned commit ${commit}: $(head -1 <<<"${dirty}")"
    if [[ "${submodules}" == "True" && -f "${dir}/.gitmodules" ]]; then
        local subs
        subs="$(git -C "${dir}" submodule status --recursive | grep '^[-+]' || true)"
        [[ -z "${subs}" ]] \
            || ff_die "a submodule of ${dir} is missing or not at the commit its parent records: $(head -1 <<<"${subs}")"
    fi
}

# --- the FFmpeg baseline ------------------------------------------------------
ff_log "fetching jellyfin-ffmpeg ${FF_FFMPEG_BASELINE} @ ${FF_FFMPEG_COMMIT}"
ff_git_at_commit "${CACHE}/git/jellyfin-ffmpeg" "${FF_FFMPEG_REPO}" "${FF_FFMPEG_COMMIT}" False
ff_log "ffmpeg source at ${FF_FFMPEG_COMMIT}"

# --- every component ----------------------------------------------------------
for row in "${FF_ROWS[@]}"; do
    IFS=$'\t' read -r name kind pin src submodules mirrors <<<"${row}"
    [[ -n "${name}" ]] || continue
    case "${kind}" in
        tar)
            dest="${CACHE}/archives/${name}-$(basename "${src}")"

            # A cached archive that does not satisfy its pin is deleted and
            # downloaded again from the pinned URL. This is the ONLY permitted
            # form of self-healing: the pin is immutable, the replacement bytes
            # face exactly the same check below, and a second mismatch is fatal.
            # Corrupt bytes are never blessed and never consumed.
            #
            # Scoped to an archive that existed BEFORE this run, which is the
            # only one that could have been restored. A file downloaded in this
            # run that fails its digest is a bad pin or a hostile mirror, and it
            # fails immediately with no second request.
            if [[ -f "${dest}" ]]; then
                cached="$(ff_sha256 "${dest}")"
                if [[ "${cached}" != "${pin}" ]]; then
                    ff_log "  ${name}: the cached archive is ${cached}, the manifest pins ${pin}; discarding it"
                    rm -f "${dest}"
                fi
            fi

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
done

# --- the source set, as a set -------------------------------------------------
# The loop above proves every DECLARED component is present and pinned. This
# proves the converse: that the cache holds nothing else. Together they are the
# inventory check the build is entitled to assume before it compiles anything.
missing=0
for base in "${!FF_EXPECT_ARCHIVE[@]}"; do
    [[ -f "${CACHE}/archives/${base}" ]] || { ff_log "missing source component: archives/${base}"; missing=1; }
done
for base in "${!FF_EXPECT_GIT[@]}"; do
    [[ -d "${CACHE}/git/${base}" ]] || { ff_log "missing source component: git/${base}"; missing=1; }
done
[[ "${missing}" -eq 0 ]] || ff_die "the source cache is incomplete"

extra="$( { find "${CACHE}/archives" -mindepth 1 -maxdepth 1 -printf 'archives/%f\n'; \
            find "${CACHE}/git" -mindepth 1 -maxdepth 1 -printf 'git/%f\n'; } | LC_ALL=C sort )"
allowed="$( { for base in "${!FF_EXPECT_ARCHIVE[@]}"; do printf 'archives/%s\n' "${base}"; done; \
              for base in "${!FF_EXPECT_GIT[@]}";     do printf 'git/%s\n' "${base}"; done; } | LC_ALL=C sort )"
if [[ "${extra}" != "${allowed}" ]]; then
    diff <(printf '%s\n' "${allowed}") <(printf '%s\n' "${extra}") >&2 || true
    ff_die "the source cache holds an entry the manifest does not declare"
fi

ff_log "every pinned source is present, matches its digest, and nothing else is present"
