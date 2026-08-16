#!/usr/bin/env bash
# Prove that adding win-x64 changed nothing about the Linux runtimes (W1-A2 / #236).
#
# W1-A2 is allowed to touch ci/ffmpeg/components.json and its validators. That is
# exactly the surface the two Linux runtimes are built from, so "the Linux
# identities are preserved" cannot be an assurance in a pull-request description;
# it has to be a check that fails.
#
# Three statements, each falsifiable:
#   1. every file the Linux build reads other than components.json is byte-for-byte
#      what master has;
#   2. the resolved configure flag list for linux-x64 and for linux-arm64 is
#      character-for-character what master resolves;
#   3. the set of components each Linux architecture builds is exactly what master
#      builds, in the same order, with the same pins.
#
# Usage: verify-linux-identity.sh [--base <ref>]

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
BASE="origin/master"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --base) BASE="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

cd "${ROOT}"
FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

if ! git rev-parse --verify --quiet "${BASE}" >/dev/null; then
    echo "  note: ${BASE} is not available in this checkout; fetching it" >&2
    git fetch --no-tags --depth=1 origin master >/dev/null 2>&1 || true
fi
git rev-parse --verify --quiet "${BASE}" >/dev/null \
    || { echo "cannot resolve ${BASE}" >&2; exit 2; }

echo "== Linux build inputs are unchanged against ${BASE}"
UNCHANGED=(
    ci/ffmpeg/ffmpeg-configure.txt
    ci/ffmpeg/ffmpeg-configure.linux-x64.txt
    ci/ffmpeg/ffmpeg-configure.linux-arm64.txt
    ci/ffmpeg/excluded-patches.txt
    ci/ffmpeg/fork-patches.json
    ci/ffmpeg/build-in-container.sh
    ci/ffmpeg/build-runtime.sh
    ci/ffmpeg/fetch-sources.sh
    ci/ffmpeg/lib.sh
    ci/ffmpeg/package-runtime.sh
    ci/ffmpeg/verify-runtime.sh
    ci/ffmpeg/verify-closure.sh
    ci/ffmpeg/accept-runtime.sh
    ci/ffmpeg/accept-hardware.sh
    ci/ffmpeg/allowed-dt-needed.txt
    ci/ffmpeg/delivered-digests.sh
    ci/ffmpeg/repro-check.sh
    .github/workflows/ffmpeg-runtime.yml
)
for f in "${UNCHANGED[@]}"; do
    if git diff --quiet "${BASE}" -- "${f}"; then
        continue
    fi
    fail "${f} differs from ${BASE}; W1-A2 does not own it"
done
[[ "${FAILURES}" -eq 0 ]] && pass "${#UNCHANGED[@]} Linux build inputs byte-identical to ${BASE}"

echo "== the resolved Linux configure flags are unchanged"
for arch in linux-x64 linux-arm64; do
    # `|| true` on every grep: linux-arm64.txt is entirely comments — it adds no
    # flag of its own and explains why — so a bare grep exits 1 there and takes
    # the whole gate down with `set -e` before it has compared anything.
    before="$( { git show "${BASE}:ci/ffmpeg/ffmpeg-configure.txt" | grep -vE '^\s*(#|$)' || true; } ; \
               { git show "${BASE}:ci/ffmpeg/ffmpeg-configure.${arch}.txt" | grep -vE '^\s*(#|$)' || true; } )"
    after="$(grep -hvE '^\s*(#|$)' ci/ffmpeg/ffmpeg-configure.txt "ci/ffmpeg/ffmpeg-configure.${arch}.txt" || true)"
    if [[ "${before}" == "${after}" ]]; then
        pass "${arch}: $(wc -l <<<"${after}") flags, identical to ${BASE}"
    else
        fail "${arch}: the resolved configure flags changed"
        diff <(printf '%s\n' "${before}") <(printf '%s\n' "${after}") >&2 || true
    fi
done

echo "== the component set each Linux architecture builds is unchanged"
git show "${BASE}:ci/ffmpeg/components.json" > /tmp/w1a2-base-components.json
for arch in linux-x64 linux-arm64; do
    if python3 - "${arch}" /tmp/w1a2-base-components.json ci/ffmpeg/components.json <<'PY'
import json, sys
arch, base_path, head_path = sys.argv[1:4]

def resolve(path):
    d = json.load(open(path))
    out = []
    for c in d["components"]:
        allowed = c.get("architectures")
        if allowed is None or arch in allowed:
            out.append((c["name"], c["sourceType"],
                        c.get("sha256") or c.get("commit"),
                        c.get("url") or c.get("repository"),
                        c.get("license"), bool(c.get("submodules", False))))
    return out

base, head = resolve(base_path), resolve(head_path)
if base != head:
    only_base = [b for b in base if b not in head]
    only_head = [h for h in head if h not in base]
    for b in only_base:
        print(f"    dropped from {arch}: {b[0]}")
    for h in only_head:
        print(f"    added to {arch}: {h[0]}")
    sys.exit(1)
print(f"    {len(head)} components, same names, pins, licences and order")
PY
    then
        pass "${arch}: component set identical to ${BASE}"
    else
        fail "${arch}: the component set changed"
    fi
done
rm -f /tmp/w1a2-base-components.json

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "LINUX IDENTITY: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "LINUX IDENTITY: PASS"
