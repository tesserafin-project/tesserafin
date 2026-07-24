#!/usr/bin/env bash
# Restore a containerised Tesserafin server's persistent state (#88 / [A2]).
#
# Restores an archive produced by docker/backup.sh into the config + data volumes.
# Verifies the SHA-256 sidecar (if present), validates the archive STRUCTURE before
# extracting (rejects absolute paths, ".." traversal and any top-level entry other
# than config/ or data/), refuses to clobber a non-empty target unless --force,
# extracts through a helper container, and re-asserts the runtime ownership
# (uid:gid 10000:10000) so the non-root server can read/write its state.
#
# Under --force the target volumes are emptied of EVERY child entry (including
# dotfiles) before extraction, while the mount roots themselves are preserved.
#
# The target server MUST be stopped during restore. If --container is given this
# script verifies it is not running (and stops it with --force).
#
# Helper image (supply-chain): the default helper is pinned to an immutable
# multi-arch busybox digest (amd64+arm64); override with --helper-image or the
# TF_HELPER_IMAGE environment variable.
#
# Usage:
#   docker/restore.sh --archive ARCHIVE.tgz --config CONFIG_VOL --data DATA_VOL \
#                     [--uid UID] [--gid GID] [--container NAME] [--force] \
#                     [--helper-image IMG]
set -euo pipefail

DEFAULT_HELPER="busybox:stable@sha256:73aaf090f3d85aa34ee199857f03fa3a95c8ede2ffd4cc2cdb5b94e566b11662"

ARCHIVE="" ; CONFIG_VOL="" ; DATA_VOL="" ; UID_R=10000 ; GID_R=10000
CONTAINER="" ; FORCE=0 ; HELPER="${TF_HELPER_IMAGE:-${DEFAULT_HELPER}}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --archive)      ARCHIVE="$2"; shift 2 ;;
    --config)       CONFIG_VOL="$2"; shift 2 ;;
    --data)         DATA_VOL="$2"; shift 2 ;;
    --uid)          UID_R="$2"; shift 2 ;;
    --gid)          GID_R="$2"; shift 2 ;;
    --container)    CONTAINER="$2"; shift 2 ;;
    --force)        FORCE=1; shift ;;
    --helper-image) HELPER="$2"; shift 2 ;;
    -h|--help)      grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

[[ -n "${ARCHIVE}" && -n "${CONFIG_VOL}" && -n "${DATA_VOL}" ]] \
  || { echo "usage: restore.sh --archive ARCHIVE --config VOL --data VOL [--force]" >&2; exit 2; }
[[ -f "${ARCHIVE}" ]] || { echo "archive not found: ${ARCHIVE}" >&2; exit 1; }
case "${UID_R}${GID_R}" in *[!0-9]*) echo "uid/gid must be numeric" >&2; exit 2 ;; esac

ARCHIVE_DIR="$(cd "$(dirname "${ARCHIVE}")" && pwd)"
ARCHIVE_BASE="$(basename "${ARCHIVE}")"
ARCHIVE_ABS="${ARCHIVE_DIR}/${ARCHIVE_BASE}"

# --- Integrity check --------------------------------------------------------
if [[ -f "${ARCHIVE_ABS}.sha256" ]]; then
  echo "== verifying archive integrity =="
  ( cd "${ARCHIVE_DIR}" && sha256sum -c "${ARCHIVE_BASE}.sha256" )
else
  echo "WARNING: no ${ARCHIVE_BASE}.sha256 sidecar — skipping integrity check" >&2
fi

# --- Structure validation (before any extraction) --------------------------
# The checksum only proves the bytes match a trusted sidecar; it says nothing
# about WHERE those bytes would extract to. Reject a hostile/corrupt layout so a
# tampered archive cannot escape the config/ + data/ roots.
echo "== validating archive structure =="
docker run --rm -v "${ARCHIVE_DIR}:/in:ro" "${HELPER}" \
  sh -eu -c '
    tar tzf "/in/'"${ARCHIVE_BASE}"'" > /tmp/list
    if grep -q "^/" /tmp/list; then
      echo "REJECT: archive contains an absolute path" >&2; exit 1; fi
    if grep -qE "(^|/)\.\.(/|\$)" /tmp/list; then
      echo "REJECT: archive contains a .. traversal entry" >&2; exit 1; fi
    # Every entry must sit under config/ or data/.
    awk -F/ "{print \$1}" /tmp/list | sort -u | while read -r top; do
      [ -z "$top" ] && continue
      case "$top" in
        config|data) ;;
        *) echo "REJECT: unexpected top-level entry: $top" >&2; exit 1 ;;
      esac
    done
  '

# --- Refuse to run against a live server -----------------------------------
if [[ -n "${CONTAINER}" ]]; then
  if [[ "$(docker inspect -f '{{.State.Running}}' "${CONTAINER}" 2>/dev/null || echo false)" == "true" ]]; then
    if [[ "${FORCE}" == 1 ]]; then
      echo "== stopping ${CONTAINER} before restore =="
      docker stop "${CONTAINER}" >/dev/null
    else
      echo "refusing to restore while ${CONTAINER} is running (use --force)" >&2; exit 1
    fi
  fi
fi

# --- Guard against clobbering populated volumes ----------------------------
NONEMPTY="$(docker run --rm \
  -v "${CONFIG_VOL}:/v/config" -v "${DATA_VOL}:/v/data" \
  "${HELPER}" sh -c 'find /v/config /v/data -mindepth 1 -print -quit 2>/dev/null')"
if [[ -n "${NONEMPTY}" && "${FORCE}" != 1 ]]; then
  echo "target volumes are not empty (e.g. ${NONEMPTY}); refusing without --force" >&2
  exit 1
fi

# --- Extract + re-assert ownership -----------------------------------------
# Under --force, delete every child entry (dotfiles included) while keeping the
# mount roots; `rm -rf /v/config/*` would leave hidden entries behind.
echo "== restoring config + data =="
docker run --rm \
  -v "${CONFIG_VOL}:/v/config" \
  -v "${DATA_VOL}:/v/data" \
  -v "${ARCHIVE_DIR}:/in:ro" \
  "${HELPER}" sh -eu -c '
    if [ "'"${FORCE}"'" = "1" ]; then
      find /v/config /v/data -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    fi
    tar xzf "/in/'"${ARCHIVE_BASE}"'" --numeric-owner -C /v
    chown -R '"${UID_R}"':'"${GID_R}"' /v/config /v/data
  '

echo "== restore complete =="
echo "  archive : ${ARCHIVE_ABS}"
echo "  config  : ${CONFIG_VOL}"
echo "  data    : ${DATA_VOL}"
echo "  owner   : ${UID_R}:${GID_R}"
echo "  Start the server container to apply any pending migrations on boot."
