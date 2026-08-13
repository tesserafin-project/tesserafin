#!/usr/bin/env bash
# Install the pinned ORAS client (#236, W1-R).
#
# The runner's preinstalled registry clients are deliberately NOT used: their
# identity is whatever the image happens to carry, which is the rolling-input
# problem this mechanism exists to remove. The archive is verified against
# ci/windows/build-inputs/tools.lock.json BEFORE it is unpacked.
#
# Usage: install-oras.sh <platform> <destination-dir>
#   platform: linux-amd64 | windows-amd64
set -euo pipefail

platform="${1:?platform required (linux-amd64 | windows-amd64)}"
dest="${2:?destination directory required}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lock="${here}/tools.lock.json"

read -r url expected binary < <(
  python3 - "$lock" "$platform" <<'PY'
import json, sys
lock = json.load(open(sys.argv[1]))
try:
    entry = lock["tools"]["oras"]["platforms"][sys.argv[2]]
except KeyError:
    sys.exit(f"tools.lock.json declares no oras for platform {sys.argv[2]!r}")
print(entry["url"], entry["archiveSha256"], entry["binary"])
PY
)

mkdir -p "$dest"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

archive="${work}/$(basename "$url")"
curl --fail --silent --show-error --location --max-time 300 --output "$archive" "$url"

actual="$(sha256sum "$archive" | cut -d' ' -f1)"
if [ "$actual" != "$expected" ]; then
  echo "W1-R TOOL HARD STOP: $(basename "$url") sha256 $actual != pinned $expected" >&2
  exit 1
fi

case "$archive" in
  *.tar.gz) tar -xzf "$archive" -C "$work" ;;
  *.zip)    unzip -q "$archive" -d "$work" ;;
  *) echo "W1-R TOOL HARD STOP: unsupported archive $archive" >&2; exit 1 ;;
esac

install -m 0755 "${work}/${binary}" "${dest}/${binary}"
echo "oras pinned $(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["tools"]["oras"]["version"])' "$lock") verified and installed at ${dest}/${binary}"
