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

# `tr -d '\r'` is not defensive tidying. On a Windows runner this script runs
# under Git for Windows' bash while `python3` is native Windows Python, which
# opens stdout in TEXT mode and terminates the line with CRLF. The carriage
# return then rides along on the LAST field, and the failure surfaces at the
# very end as
#
#     install: cannot stat '/tmp/tmp.AB3gPnArLz/oras.exe'$'\r'
#
# which reads like a missing file rather than a line ending. The linux-amd64
# path never saw it, so the windows-amd64 platform was broken from the day it
# was written until the first job actually asked for it.
read -r url expected binary < <(
  python3 - "$lock" "$platform" <<'PY' | tr -d '\r'
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
version="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["tools"]["oras"]["version"])' "$lock" | tr -d '\r')"
echo "oras pinned ${version} verified and installed at ${dest}/${binary}"
