#!/usr/bin/env bash
# Cold backup of a containerised Tesserafin server's persistent state (#88 / [A2]).
#
# Produces a single portable archive of the stateful named volumes (config + data)
# plus a JSON manifest and a SHA-256 sidecar. Cache is disposable and is NOT backed
# up; /media is a read-only external mount and is never part of server state.
#
# Confidentiality: the archive contains databases, users and access tokens. Output
# is written with umask 077, streamed from the helper container to a host file
# created by the invoking user, so the archive + sidecars are owned by that user
# and are NOT group/world-readable by default.
#
# Consistency: SQLite is only crash-consistent while the server writes. For a
# guaranteed-consistent snapshot, stop the server first. If --container is given
# this script stops that container before reading the volumes and restarts it after
# (only if it was running to begin with). A failed restart is fatal: the command
# exits non-zero and states that the archive exists but the server is stopped.
#
# The volumes are read through a throwaway helper container, so this works for
# docker *named volumes* (not just host bind mounts) and needs no host-side root.
#
# Usage:
#   docker/backup.sh --out ARCHIVE.tgz --config CONFIG_VOL --data DATA_VOL \
#                    [--container NAME] [--helper-image IMG]
#
#   --out         destination archive path on the host (a .sha256 sidecar and a
#                 .manifest.json sidecar are written next to it)
#   --config      docker volume (or host path) mounted at /config in the server
#   --data        docker volume (or host path) mounted at /data in the server
#   --container   server container to quiesce around the snapshot (recommended)
#   --helper-image  image used for the tar helper. Default is an immutable,
#                 multi-architecture (amd64+arm64) busybox digest; override with
#                 any image that provides POSIX tar. See "Helper image" below.
#
# Helper image (supply-chain): the default helper is pinned to an immutable
# manifest-list digest, not a floating tag, so backup tooling does not silently
# change under it. The digest covers linux/amd64 and linux/arm64/v8 (verified with
# `docker buildx imagetools inspect`). Override with --helper-image or the
# TF_HELPER_IMAGE environment variable.
#
# Restart command (testing/override hook): the post-snapshot restart runs
# `${TF_RESTART_CMD} <container>` (default `docker start`). Operators driving the
# server through compose/systemd can point this at their own start command; the
# A2 round-trip test uses it to exercise deterministic restart-failure handling.
set -euo pipefail

# Immutable multi-arch busybox (see "Helper image" above).
DEFAULT_HELPER="busybox:stable@sha256:73aaf090f3d85aa34ee199857f03fa3a95c8ede2ffd4cc2cdb5b94e566b11662"

OUT="" ; CONFIG_VOL="" ; DATA_VOL="" ; CONTAINER=""
HELPER="${TF_HELPER_IMAGE:-${DEFAULT_HELPER}}"
RESTART_CMD="${TF_RESTART_CMD:-docker start}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out)          OUT="$2"; shift 2 ;;
    --config)       CONFIG_VOL="$2"; shift 2 ;;
    --data)         DATA_VOL="$2"; shift 2 ;;
    --container)    CONTAINER="$2"; shift 2 ;;
    --helper-image) HELPER="$2"; shift 2 ;;
    -h|--help)      grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

[[ -n "${OUT}" && -n "${CONFIG_VOL}" && -n "${DATA_VOL}" ]] \
  || { echo "usage: backup.sh --out ARCHIVE --config VOL --data VOL [--container NAME]" >&2; exit 2; }

# Private-by-default: archive + sidecars are created 0600, owned by the invoking user.
umask 077

OUT_DIR="$(cd "$(dirname "${OUT}")" && pwd)"
OUT_BASE="$(basename "${OUT}")"
OUT_ABS="${OUT_DIR}/${OUT_BASE}"
# Stream into a clearly-invalid partial name; only rename to the real archive once
# the whole snapshot succeeded, so a mid-stream failure never looks like a backup.
TMP_OUT="${OUT_ABS}.partial.$$"

WAS_RUNNING=0
PUBLISHED=0
cleanup() {
  # On any early/error exit: drop partial output and (best-effort) restart the
  # server. The authoritative restart with its exit-code handling runs in-line
  # below; this trap only covers failures that abort before we reach it.
  [[ "${PUBLISHED}" == 1 ]] || rm -f "${TMP_OUT}" "${OUT_ABS}.sha256" "${OUT_ABS}.manifest.json"
  if [[ "${WAS_RUNNING}" == 1 ]]; then
    if [[ "$(docker inspect -f '{{.State.Running}}' "${CONTAINER}" 2>/dev/null || echo false)" != "true" ]]; then
      ${TF_RESTART_CMD:-docker start} "${CONTAINER}" >/dev/null 2>&1 || true
    fi
  fi
}
trap cleanup EXIT

# --- Quiesce the server for a consistent SQLite snapshot -------------------
if [[ -n "${CONTAINER}" ]]; then
  if [[ "$(docker inspect -f '{{.State.Running}}' "${CONTAINER}" 2>/dev/null || echo false)" == "true" ]]; then
    WAS_RUNNING=1
    echo "== stopping ${CONTAINER} for a consistent snapshot =="
    docker stop "${CONTAINER}" >/dev/null
  fi
fi

# --- Snapshot config + data through a helper container ---------------------
# --numeric-owner keeps the container uid/gid (10000) in the archive so restore
# reproduces the exact ownership without needing the tesserafin account on the host.
# The tar stream is written to the HOST file (owned by the invoking user); the
# helper never writes into the host output directory as root.
echo "== archiving config + data =="
if ! docker run --rm \
      -v "${CONFIG_VOL}:/v/config:ro" \
      -v "${DATA_VOL}:/v/data:ro" \
      "${HELPER}" \
      tar czf - --numeric-owner -C /v config data > "${TMP_OUT}"; then
  rm -f "${TMP_OUT}"
  echo "backup FAILED: archive step errored; no valid output written" >&2
  exit 1
fi

# --- Sidecars: integrity + manifest (written before publishing the archive) ---
SHA="$(sha256sum "${TMP_OUT}" | awk '{print $1}')"
SIZE="$(stat -c %s "${TMP_OUT}")"
echo "${SHA}  ${OUT_BASE}" > "${OUT_ABS}.sha256"

# Emit the manifest with a real JSON encoder so free-form host paths / container
# names cannot break the document (no unescaped interpolation).
CONTAINER="${CONTAINER}" CONFIG_VOL="${CONFIG_VOL}" DATA_VOL="${DATA_VOL}" \
OUT_BASE="${OUT_BASE}" SHA="${SHA}" SIZE="${SIZE}" HELPER="${HELPER}" \
python3 - > "${OUT_ABS}.manifest.json" <<'PY'
import json, os
print(json.dumps({
    "tool": "tesserafin backup.sh",
    "format": "tar.gz (--numeric-owner)",
    "contents": ["config", "data"],
    "excluded": ["cache", "media"],
    "expected_uid_gid": "10000:10000",
    "helper_image": os.environ["HELPER"],
    "source_container": os.environ["CONTAINER"],
    "config_volume": os.environ["CONFIG_VOL"],
    "data_volume": os.environ["DATA_VOL"],
    "archive": os.environ["OUT_BASE"],
    "sha256": os.environ["SHA"],
    "size_bytes": int(os.environ["SIZE"]),
}, indent=2))
PY

# Publish the archive atomically now that its sidecars are in place.
mv "${TMP_OUT}" "${OUT_ABS}"
PUBLISHED=1

# --- Restart the server, and FAIL LOUDLY if it cannot be restored ----------
RESTART_FAILED=0
if [[ "${WAS_RUNNING}" == 1 ]]; then
  echo "== restarting ${CONTAINER} =="
  if ! ${RESTART_CMD} "${CONTAINER}" >/dev/null; then
    RESTART_FAILED=1
  fi
fi
trap - EXIT

echo "== backup complete =="
echo "  archive : ${OUT_ABS} (${SIZE} bytes)"
echo "  sha256  : ${SHA}"
echo "  manifest: ${OUT_ABS}.manifest.json"

if [[ "${RESTART_FAILED}" == 1 ]]; then
  echo "ERROR: the backup archive exists at ${OUT_ABS}, but ${CONTAINER} could NOT be" >&2
  echo "       restarted and remains STOPPED. Investigate and start it manually." >&2
  exit 3
fi
