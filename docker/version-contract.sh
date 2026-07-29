#!/usr/bin/env bash
# The single source of image-version and image-tag truth for Tesserafin (#92 / [A6]).
#
# Every other consumer — docker/build-clean.sh, docker-bake.hcl, the release
# workflow, the container gates — asks THIS script. Nothing else derives a tag.
# docker-bake.hcl deliberately contains no tag logic of its own; it consumes the
# TAGS variable this script emits, and docker/version-contract.test.sh asserts
# that `docker buildx bake --print` reproduces exactly these tags.
#
# Canonical version source
#   SharedVersion.cs  ->  [assembly: AssemblyVersion("MAJOR.MINOR.PATCH")]
# An assembly version is numeric-only, so it carries the RELEASE CORE and never a
# SemVer pre-release suffix. A pre-release is expressed by the git release tag
# (`v12.1.0-rc.1`); its core MUST equal the canonical source version.
#
# Usage:
#   version-contract.sh version                       print the canonical core version
#   version-contract.sh tags   [options]              print one image tag per line
#   version-contract.sh env    [options]              print KEY=VALUE build inputs
#   version-contract.sh verify-tag <git-tag> [opts]   assert a git tag matches the source
#   version-contract.sh check  [options]              run every applicable assertion
#
# Options:
#   --channel dev|prerelease|stable   tag class to derive (default: dev)
#   --release-tag <tag>               explicit git release tag (required for
#                                     prerelease/stable), with or without a `v` prefix
#   --commit <sha>                    40-char commit; default `git rev-parse HEAD`
#   --registry <ref>                  default $REGISTRY or the project GHCR repo
#   --allow-dirty                     permit a release from a dirty tree; the
#                                     override is printed to stderr and recorded in
#                                     the emitted TESSERAFIN_DIRTY_RELEASE=1 variable
#
# Exit codes: 0 success, 1 contract violation, 2 usage error.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SHARED_VERSION_FILE="${REPO_ROOT}/SharedVersion.cs"
# The CANONICAL v1+ server package. The inherited pre-v1 development images live
# in ghcr.io/tesserafin/tesserafin, which is a frozen archive: it never
# receives another tag. See docs/versioning-policy.md §2.
#
# Note that the archive reference is a strict PREFIX of this one. Anything that
# has to tell the two apart compares the full repository reference or stops at
# the `:`/`@` boundary; a substring test for "tesserafin" matches both.
DEFAULT_REGISTRY="ghcr.io/tesserafin/tesserafin-server"
ARCHIVE_REGISTRY="ghcr.io/tesserafin/tesserafin"

die() { echo "version-contract: $*" >&2; exit 1; }
usage_die() { echo "version-contract: $*" >&2; exit 2; }

# --- canonical version -------------------------------------------------------

# Reads MAJOR.MINOR.PATCH from SharedVersion.cs and validates it. Anything the
# regex does not match — a 2-part or 4-part assembly version, a wildcard, a
# missing file — is a contract violation, not a fallback.
canonical_version() {
  [[ -f "${SHARED_VERSION_FILE}" ]] || die "canonical version source not found: ${SHARED_VERSION_FILE}"
  local raw
  raw="$(sed -nE 's/^\[assembly: ?AssemblyVersion\("([^"]*)"\)\].*/\1/p' "${SHARED_VERSION_FILE}" | head -1)"
  [[ -n "${raw}" ]] || die "no [assembly: AssemblyVersion(\"...\")] in ${SHARED_VERSION_FILE}"
  is_core_version "${raw}" || die "canonical version '${raw}' is not a MAJOR.MINOR.PATCH SemVer core"
  printf '%s\n' "${raw}"
}

# SemVer 2.0.0 version core: three dot-separated numeric identifiers, no leading
# zeroes (0 itself is fine), no pre-release and no build metadata.
is_core_version() {
  [[ "$1" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]
}

# SemVer 2.0.0 pre-release: dot-separated alphanumeric/hyphen identifiers, and a
# purely numeric identifier must not carry a leading zero.
is_prerelease_suffix() {
  [[ "$1" =~ ^(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*$ ]]
}

# Splits a full release version into its core and (possibly empty) pre-release.
# Build metadata (`+meta`) is rejected: it is not representable in a Docker tag.
split_release_version() { # $1 = 12.1.0 | 12.1.0-rc.1  -> sets RV_CORE, RV_PRE
  local v="$1"
  [[ "${v}" != *"+"* ]] || die "build metadata is not allowed in a release version: '${v}'"
  if [[ "${v}" == *-* ]]; then
    RV_CORE="${v%%-*}"
    RV_PRE="${v#*-}"
    is_prerelease_suffix "${RV_PRE}" || die "'${RV_PRE}' is not a valid SemVer pre-release identifier"
  else
    RV_CORE="${v}"
    RV_PRE=""
  fi
  is_core_version "${RV_CORE}" || die "'${RV_CORE}' is not a MAJOR.MINOR.PATCH SemVer core"
}

# --- provenance --------------------------------------------------------------

resolve_commit() {
  local c="${1:-}"
  if [[ -z "${c}" ]]; then
    c="$(git -C "${REPO_ROOT}" rev-parse HEAD 2>/dev/null || true)"
  fi
  [[ -n "${c}" ]] || die "missing commit provenance: not a git checkout and no --commit given"
  [[ "${c}" =~ ^[0-9a-f]{40}$ ]] || die "commit provenance '${c}' is not a full 40-character lowercase SHA"
  printf '%s\n' "${c}"
}

tree_is_dirty() {
  git -C "${REPO_ROOT}" rev-parse --git-dir >/dev/null 2>&1 || return 1
  [[ -n "$(git -C "${REPO_ROOT}" status --porcelain)" ]]
}

# --- tag derivation ----------------------------------------------------------
#
# dev         <version>-dev.<12-char commit>   immutable, one exact commit
#             sha-<40-char commit>             immutable, one exact commit
# prerelease  <version>-<pre>                  immutable, one exact release
#             preview                          MUTABLE channel; never `latest`
#             sha-<40-char commit>             immutable
# stable      <version>                        immutable
#             <major>.<minor>                  MUTABLE
#             <major>                          MUTABLE
#             latest                           MUTABLE; stable releases only
#             sha-<40-char commit>             immutable
derive_tags() { # $1=channel $2=canonical core $3=commit $4=release tag ("" for dev) $5=registry
  local channel="$1" core="$2" commit="$3" release="$4" registry="$5"
  local short="${commit:0:12}"
  local -a tags=()

  case "${channel}" in
    dev)
      [[ -z "${release}" ]] || die "--release-tag is not valid for the dev channel (dev tags identify a commit, not a release)"
      tags+=("${registry}:${core}-dev.${short}" "${registry}:sha-${commit}")
      ;;
    prerelease)
      [[ -n "${release}" ]] || usage_die "--release-tag is required for the prerelease channel"
      split_release_version "${release}"
      [[ -n "${RV_PRE}" ]] || die "release tag '${release}' has no pre-release identifier — use --channel stable"
      [[ "${RV_CORE}" == "${core}" ]] || die "release tag core '${RV_CORE}' != canonical version '${core}' (SharedVersion.cs)"
      tags+=("${registry}:${RV_CORE}-${RV_PRE}" "${registry}:preview" "${registry}:sha-${commit}")
      ;;
    stable)
      [[ -n "${release}" ]] || usage_die "--release-tag is required for the stable channel"
      split_release_version "${release}"
      [[ -z "${RV_PRE}" ]] || die "release tag '${release}' is a pre-release — a pre-release must never publish stable or 'latest' tags"
      [[ "${RV_CORE}" == "${core}" ]] || die "release tag core '${RV_CORE}' != canonical version '${core}' (SharedVersion.cs)"
      local major="${RV_CORE%%.*}" rest="${RV_CORE#*.}"
      local minor="${rest%%.*}"
      tags+=("${registry}:${RV_CORE}" "${registry}:${major}.${minor}" "${registry}:${major}"
             "${registry}:latest" "${registry}:sha-${commit}")
      ;;
    *)
      usage_die "unknown --channel '${channel}' (expected dev, prerelease or stable)"
      ;;
  esac

  printf '%s\n' "${tags[@]}"
}

# The pre-v1 archive is frozen: it receives no further tag of any class, and in
# particular no public-release alias. Because ARCHIVE_REGISTRY is a strict prefix
# of DEFAULT_REGISTRY, the comparison is on the repository reference alone — the
# part before the tag separator — and never a substring test.
assert_not_archive() { # $1..$n = tags
  local t repo
  for t in "$@"; do
    # Strip a digest suffix FIRST: `%:*` alone would leave `…/tesserafin@sha256`
    # for a digest reference, which compares unequal and would let the guard pass.
    repo="${t%%@*}"
    repo="${repo%:*}"
    if [[ "${repo}" == "${ARCHIVE_REGISTRY}" ]]; then
      die "refusing to derive '${t}': ${ARCHIVE_REGISTRY} is the frozen pre-v1 archive and never receives another tag (docs/versioning-policy.md §2)"
    fi
  done
}

# `latest` is only ever reachable through the stable branch above. This is the
# belt-and-braces assertion that keeps that true if derive_tags is ever edited.
assert_no_latest() { # $1=channel, remaining = tags
  local channel="$1"; shift
  local t
  for t in "$@"; do
    if [[ "${t##*:}" == "latest" && "${channel}" != "stable" ]]; then
      die "channel '${channel}' attempted to publish 'latest' — only an explicit stable release may move it"
    fi
  done
}

# --- argument parsing --------------------------------------------------------

CHANNEL="dev"
RELEASE_TAG=""
COMMIT_ARG=""
REGISTRY_ARG="${REGISTRY:-${DEFAULT_REGISTRY}}"
ALLOW_DIRTY=0

parse_options() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --channel)     [[ $# -ge 2 ]] || usage_die "--channel needs a value";     CHANNEL="$2"; shift 2 ;;
      --release-tag) [[ $# -ge 2 ]] || usage_die "--release-tag needs a value"; RELEASE_TAG="${2#v}"; shift 2 ;;
      --commit)      [[ $# -ge 2 ]] || usage_die "--commit needs a value";      COMMIT_ARG="$2"; shift 2 ;;
      --registry)    [[ $# -ge 2 ]] || usage_die "--registry needs a value";    REGISTRY_ARG="$2"; shift 2 ;;
      --allow-dirty) ALLOW_DIRTY=1; shift ;;
      *) usage_die "unknown option: $1" ;;
    esac
  done
}

# A release publication from a dirty tree cannot be reproduced from its commit,
# so it is refused unless the operator opts in — and the opt-in is loud.
enforce_clean_tree_for_release() {
  [[ "${CHANNEL}" == "dev" ]] && return 0
  tree_is_dirty || return 0
  if [[ "${ALLOW_DIRTY}" == "1" ]]; then
    echo "version-contract: WARNING — publishing release '${RELEASE_TAG}' from a DIRTY working tree (--allow-dirty)." >&2
    git -C "${REPO_ROOT}" status --porcelain >&2
    return 0
  fi
  die "refusing to derive ${CHANNEL} tags from a dirty working tree; commit the changes or pass --allow-dirty"
}

# --- subcommands -------------------------------------------------------------

cmd_version() { canonical_version; }

cmd_tags() {
  local core commit
  core="$(canonical_version)"
  commit="$(resolve_commit "${COMMIT_ARG}")"
  enforce_clean_tree_for_release
  # Command substitution, NOT `mapfile < <(...)`: a process substitution runs in a
  # subshell whose non-zero exit mapfile happily ignores, so every die() inside
  # derive_tags would be swallowed and the contract would fail OPEN.
  local tags_raw
  tags_raw="$(derive_tags "${CHANNEL}" "${core}" "${commit}" "${RELEASE_TAG}" "${REGISTRY_ARG}")" || exit $?
  local -a tags
  mapfile -t tags <<<"${tags_raw}"
  assert_not_archive "${tags[@]}"
  assert_no_latest "${CHANNEL}" "${tags[@]}"
  printf '%s\n' "${tags[@]}"
}

# Deterministic KEY=VALUE inputs for docker-bake.hcl and workflow steps.
# SOURCE_DATE_EPOCH/BUILD_DATE come from the commit, never from the wall clock.
cmd_env() {
  local core commit epoch build_date
  core="$(canonical_version)"
  commit="$(resolve_commit "${COMMIT_ARG}")"
  enforce_clean_tree_for_release
  epoch="$(git -C "${REPO_ROOT}" log -1 --format=%ct "${commit}" 2>/dev/null || true)"
  [[ -n "${epoch}" ]] || die "cannot read the commit time of ${commit} (missing commit provenance)"
  build_date="$(date -u -d "@${epoch}" +%Y-%m-%dT%H:%M:%SZ)"

  local tags_raw
  tags_raw="$(derive_tags "${CHANNEL}" "${core}" "${commit}" "${RELEASE_TAG}" "${REGISTRY_ARG}")" || exit $?
  local -a tags
  mapfile -t tags <<<"${tags_raw}"
  assert_not_archive "${tags[@]}"
  assert_no_latest "${CHANNEL}" "${tags[@]}"

  local joined
  joined="$(IFS=,; printf '%s' "${tags[*]}")"
  printf 'VERSION=%s\n'           "${core}"
  printf 'VCS_REF=%s\n'           "${commit}"
  printf 'SOURCE_DATE_EPOCH=%s\n' "${epoch}"
  printf 'BUILD_DATE=%s\n'        "${build_date}"
  printf 'REGISTRY=%s\n'          "${REGISTRY_ARG}"
  printf 'CHANNEL=%s\n'           "${CHANNEL}"
  printf 'TAGS=%s\n'              "${joined}"
  printf 'PRIMARY_TAG=%s\n'       "${tags[0]}"
  if [[ "${ALLOW_DIRTY}" == "1" ]] && tree_is_dirty; then
    printf 'TESSERAFIN_DIRTY_RELEASE=1\n'
  fi
}

cmd_verify_tag() { # $1 = git tag, with or without a leading `v`
  local given="${1#v}" core
  core="$(canonical_version)"
  split_release_version "${given}"
  [[ "${RV_CORE}" == "${core}" ]] \
    || die "git tag 'v${given}' has core '${RV_CORE}' but SharedVersion.cs says '${core}'"
  if [[ -n "${RV_PRE}" ]]; then
    echo "OK: git tag 'v${given}' is a PRE-RELEASE of canonical version ${core} (must not move 'latest')"
  else
    echo "OK: git tag 'v${given}' matches canonical version ${core} (stable)"
  fi
}

# Everything that can be asserted without building an image.
cmd_check() {
  local core commit
  core="$(canonical_version)"
  commit="$(resolve_commit "${COMMIT_ARG}")"
  echo "canonical version : ${core}   (SharedVersion.cs)"
  echo "commit provenance : ${commit}"
  if [[ -n "${RELEASE_TAG}" ]]; then
    cmd_verify_tag "${RELEASE_TAG}"
  fi
  enforce_clean_tree_for_release
  local tags_raw
  tags_raw="$(derive_tags "${CHANNEL}" "${core}" "${commit}" "${RELEASE_TAG}" "${REGISTRY_ARG}")" || exit $?
  local -a tags
  mapfile -t tags <<<"${tags_raw}"
  assert_not_archive "${tags[@]}"
  assert_no_latest "${CHANNEL}" "${tags[@]}"
  echo "channel           : ${CHANNEL}"
  echo "tags              :"
  printf '  %s\n' "${tags[@]}"
}

main() {
  [[ $# -ge 1 ]] || usage_die "no subcommand; try: version | tags | env | verify-tag | check"
  local sub="$1"; shift
  case "${sub}" in
    version)    parse_options "$@"; cmd_version ;;
    tags)       parse_options "$@"; cmd_tags ;;
    env)        parse_options "$@"; cmd_env ;;
    check)      parse_options "$@"; cmd_check ;;
    verify-tag)
      [[ $# -ge 1 ]] || usage_die "verify-tag needs a git tag"
      local tag="$1"; shift
      parse_options "$@"
      cmd_verify_tag "${tag}"
      ;;
    -h|--help|help) sed -n '2,32p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
    *) usage_die "unknown subcommand '${sub}'" ;;
  esac
}

main "$@"
