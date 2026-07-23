#!/usr/bin/env bash
# Cold backup of a containerised Tesserafin server's persistent state (#88 / [A2]).
#
# Produces a single portable archive of the stateful named volumes (config + data)
# plus a JSON manifest and a SHA-256 sidecar. Cache is disposable and is NOT backed
# up; /media is a read-only external mount and is never part of server state.
#
# Consistency: SQLite is only crash-consistent while the server writes. For a
# guaranteed-consistent snapshot, stop the server first. If --container is given
# this script stops that container before reading the volumes and restarts it after
# (only if it was running to begin with).
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
#   --helper-image  image used for the tar helper (default: busybox:stable)
set -euo pipefail

OUT="" ; CONFIG_VOL="" ; DATA_VOL="" ; CONTAINER="" ; HELPER="busybox:stable"

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

OUT_DIR="$(cd "$(dirname "${OUT}")" && pwd)"
OUT_BASE="$(basename "${OUT}")"
OUT_ABS="${OUT_DIR}/${OUT_BASE}"

# --- Quiesce the server for a consistent SQLite snapshot -------------------
WAS_RUNNING=0
if [[ -n "${CONTAINER}" ]]; then
  if [[ "$(docker inspect -f '{{.State.Running}}' "${CONTAINER}" 2>/dev/null || echo false)" == "true" ]]; then
    WAS_RUNNING=1
    echo "== stopping ${CONTAINER} for a consistent snapshot =="
    docker stop "${CONTAINER}" >/dev/null
  fi
fi
restart_if_needed() {
  if [[ "${WAS_RUNNING}" == 1 ]]; then
    echo "== restarting ${CONTAINER} =="
    docker start "${CONTAINER}" >/dev/null || true
  fi
}
trap restart_if_needed EXIT

# --- Snapshot config + data through a helper container ---------------------
# --numeric-owner keeps the container uid/gid (10000) in the archive so restore
# reproduces the exact ownership without needing the tesserafin account on the host.
echo "== archiving config + data =="
docker run --rm \
  -v "${CONFIG_VOL}:/v/config:ro" \
  -v "${DATA_VOL}:/v/data:ro" \
  -v "${OUT_DIR}:/out" \
  "${HELPER}" \
  tar czf "/out/${OUT_BASE}" --numeric-owner -C /v config data

restart_if_needed
trap - EXIT

# --- Sidecars: integrity + manifest ----------------------------------------
SHA="$(sha256sum "${OUT_ABS}" | awk '{print $1}')"
echo "${SHA}  ${OUT_BASE}" > "${OUT_ABS}.sha256"

SIZE="$(stat -c %s "${OUT_ABS}")"
cat > "${OUT_ABS}.manifest.json" <<JSON
{
  "tool": "tesserafin backup.sh",
  "format": "tar.gz (--numeric-owner)",
  "contents": ["config", "data"],
  "excluded": ["cache", "media"],
  "expected_uid_gid": "10000:10000",
  "source_container": "${CONTAINER}",
  "config_volume": "${CONFIG_VOL}",
  "data_volume": "${DATA_VOL}",
  "archive": "${OUT_BASE}",
  "sha256": "${SHA}",
  "size_bytes": ${SIZE}
}
JSON

echo "== backup complete =="
echo "  archive : ${OUT_ABS} (${SIZE} bytes)"
echo "  sha256  : ${SHA}"
echo "  manifest: ${OUT_ABS}.manifest.json"
