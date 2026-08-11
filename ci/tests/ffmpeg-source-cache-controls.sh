#!/usr/bin/env bash
# Negative controls for the VERIFIED SOURCE cache (F0-CI1 / #232).
#
# Usage: ci/tests/ffmpeg-source-cache-controls.sh
#
# ci/tests/ffmpeg-controls.sh proves the 17 gates of the runtime itself and is
# not touched by this file. These controls prove one narrower claim: that a
# RESTORED source cache buys no trust. Every one of them hands fetch-sources.sh
# a cache that has been tampered with in a specific way and requires it either
# to replace the bad bytes with bytes that satisfy the original immutable pin,
# or to stop.
#
# The pin is never adjusted to match what is on disk. There is no path through
# this file where a corrupt cache entry is blessed.
#
# Hermetic by construction. The FFmpeg baseline is stood in for by a local git
# repository, so the suite does not clone 127 MB eleven times to test cache
# logic that has nothing to do with which repository it came from. Exactly one
# real pinned component is used — zlib, 1.3 MB — so the genuine curl path and
# the genuine SHA-256 comparison are exercised rather than simulated.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=ci/ffmpeg/lib.sh
source "${ROOT}/ci/ffmpeg/lib.sh"

LAB="$(mktemp -d)"
trap 'rm -rf "${LAB}"' EXIT
PASSED=0; FAILED=0

pass() { PASSED=$((PASSED + 1)); echo "  ok   $1"; }
fail() { FAILED=$((FAILED + 1)); echo "  FAIL $1 — $2"; }

# --- a stand-in origin for the FFmpeg baseline --------------------------------
ORIGIN="${LAB}/baseline"
git init -q "${ORIGIN}"
git -C "${ORIGIN}" config user.email control@tesserafin.invalid
git -C "${ORIGIN}" config user.name  "F0 control"
# Fetching an exact commit over the local transport needs upload-pack to answer
# for a SHA that is not a branch tip, exactly as GitHub does for the real pins.
git -C "${ORIGIN}" config uploadpack.allowAnySHA1InWant true
printf 'pinned\n' > "${ORIGIN}/VERSION"
git -C "${ORIGIN}" add VERSION
git -C "${ORIGIN}" commit -qm pinned
PINNED_COMMIT="$(git -C "${ORIGIN}" rev-parse HEAD)"
printf 'not pinned\n' > "${ORIGIN}/VERSION"
git -C "${ORIGIN}" commit -qam "not pinned"
OTHER_COMMIT="$(git -C "${ORIGIN}" rev-parse HEAD)"

# --- the manifest these controls are pointed at -------------------------------
MANIFEST="${LAB}/components.json"
python3 - "${ROOT}/ci/ffmpeg/components.json" "${MANIFEST}" "${ORIGIN}" "${PINNED_COMMIT}" <<'PY'
import json, sys
src, dest, origin, commit = sys.argv[1:5]
real = json.load(open(src))
# One real pinned tar component, carried over verbatim: same URL, same SHA-256,
# same mirrors. The smallest one, because this suite tests cache handling and
# not download throughput.
tars = [c for c in real["components"] if c["sourceType"] == "tar"]
zlib = next(c for c in tars if c["name"] == "zlib")
json.dump({
    "buildRevision": real["buildRevision"],
    "ffmpeg": {"repository": origin, "commit": commit, "baseline": "control"},
    "components": [zlib],
}, open(dest, "w"))
PY
ARCHIVE_NAME="$(python3 -c '
import json, sys, os
c = json.load(open(sys.argv[1]))["components"][0]
print(c["name"] + "-" + os.path.basename(c["url"]))' "${MANIFEST}")"
ARCHIVE_PIN="$(python3 -c '
import json, sys
print(json.load(open(sys.argv[1]))["components"][0]["sha256"])' "${MANIFEST}")"

# Runs the fetcher and records BOTH its output and its exit status without
# aborting the suite. A control whose fetch is supposed to fail and a control
# whose fetch is supposed to repair the cache have to be judged the same way:
# by what the cache holds afterwards, not by whether the suite survived.
OUT=""; RC=0
run() { # <cache> [manifest] -> sets OUT and RC
    RC=0
    OUT="$(FF_COMPONENTS="${2:-${MANIFEST}}" "${ROOT}/ci/ffmpeg/fetch-sources.sh" --cache "$1" 2>&1)" || RC=$?
}

# --- a warm cache every control starts from -----------------------------------
WARM="${LAB}/warm"
run "${WARM}"; out="${OUT}"
if [[ "${RC}" -ne 0 ]]; then
    echo "the control suite could not populate a clean cache; nothing below is meaningful" >&2
    printf '%s\n' "${out}" >&2
    exit 1
fi

echo "== the cache-miss path"
if grep -q "fetching ${ARCHIVE_NAME%%-*}" <<<"${out}" \
   && [[ "$(ff_sha256 "${WARM}/archives/${ARCHIVE_NAME}")" == "${ARCHIVE_PIN}" ]] \
   && [[ "$(git -C "${WARM}/git/jellyfin-ffmpeg" rev-parse HEAD)" == "${PINNED_COMMIT}" ]]; then
    pass "C1 a cache miss performs the pinned fetch"
else
    fail "C1 a cache miss performs the pinned fetch" "the cold fetch did not produce the pinned bytes"
fi

# A copy per control, so one control's damage cannot be another's input.
lab_copy() { local d="${LAB}/$1"; rm -rf "${d}"; cp -a "${WARM}" "${d}"; printf '%s\n' "${d}"; }

echo "== a cache hit is re-verified, not trusted"
c="$(lab_copy hit)"
run "${c}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]] && grep -q "matches its digest" <<<"${out}" \
   && ! grep -q "^== fetching zlib" <<<"${out}"; then
    pass "C2 a cache hit re-verifies every component without downloading it again"
else
    fail "C2 a cache hit re-verifies every component without downloading it again" \
         "verification did not run, or the bytes were fetched again"
fi

# The verification above happens in fetch-sources.sh, which build-runtime.sh
# runs on the host BEFORE it starts the container. Asserted structurally,
# because "before" is an ordering property that no single run can demonstrate.
f="${ROOT}/ci/ffmpeg/build-runtime.sh"
if [[ "$(grep -n 'fetch-sources.sh' "${f}" | head -1 | cut -d: -f1)" \
      -lt "$(grep -n '^docker run' "${f}" | head -1 | cut -d: -f1)" ]]; then
    pass "C3 verification is ordered before the build, not after it"
else
    fail "C3 verification is ordered before the build, not after it" \
         "build-runtime.sh no longer fetches and verifies before it runs the container"
fi

echo "== corrupted restored bytes"
c="$(lab_copy corrupt-archive)"
printf 'not the pinned bytes' > "${c}/archives/${ARCHIVE_NAME}"
run "${c}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]] \
   && [[ "$(ff_sha256 "${c}/archives/${ARCHIVE_NAME}")" == "${ARCHIVE_PIN}" ]] \
   && grep -q "discarding it" <<<"${out}"; then
    pass "C4 a corrupted cached archive is discarded and replaced from the pin"
else
    fail "C4 a corrupted cached archive is discarded and replaced from the pin" \
         "the corrupt archive survived, or was accepted"
fi

# The same corruption, with a pin the replacement cannot satisfy. Self-healing
# must not degrade into "download until something passes": one replacement is
# attempted against the ORIGINAL pin, and a second mismatch is fatal.
c="$(lab_copy corrupt-unfixable)"
doctored="${LAB}/impossible.json"
python3 -c '
import json, sys
j = json.load(open(sys.argv[1]))
j["components"][0]["sha256"] = "0" * 64
json.dump(j, open(sys.argv[2], "w"))' "${MANIFEST}" "${doctored}"
printf 'not the pinned bytes' > "${c}/archives/${ARCHIVE_NAME}"
run "${c}" "${doctored}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]]; then
    fail "C5 a cached archive that cannot satisfy its pin stops the build" "the fetch succeeded"
elif grep -q "checksum mismatch" <<<"${out}"; then
    pass "C5 a cached archive that cannot satisfy its pin stops the build"
else
    fail "C5 a cached archive that cannot satisfy its pin stops the build" \
         "it failed, but not with a checksum mismatch"
fi

echo "== restored git trees"
c="$(lab_copy wrong-commit)"
git -C "${c}/git/jellyfin-ffmpeg" fetch -q --depth=1 origin "${OTHER_COMMIT}"
git -C "${c}/git/jellyfin-ffmpeg" checkout -q "${OTHER_COMMIT}"
run "${c}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]] \
   && [[ "$(git -C "${c}/git/jellyfin-ffmpeg" rev-parse HEAD)" == "${PINNED_COMMIT}" ]] \
   && [[ "$(cat "${c}/git/jellyfin-ffmpeg/VERSION")" == "pinned" ]]; then
    pass "C6 a cached git tree at the wrong commit is never consumed"
else
    fail "C6 a cached git tree at the wrong commit is never consumed" \
         "the build would have compiled the unpinned commit"
fi

# The delivered corresponding source is the WORKING TREE — ff_deterministic_tar
# excludes .git — so a tree parked at the right commit with an altered file is
# the attack that a HEAD check alone cannot see.
c="$(lab_copy tampered-tree)"
printf 'backdoored\n' > "${c}/git/jellyfin-ffmpeg/VERSION"
printf 'extra\n' > "${c}/git/jellyfin-ffmpeg/EXTRA"
run "${c}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]] \
   && [[ "$(cat "${c}/git/jellyfin-ffmpeg/VERSION")" == "pinned" ]] \
   && [[ ! -e "${c}/git/jellyfin-ffmpeg/EXTRA" ]]; then
    pass "C7 a cached git tree altered at the pinned commit is never consumed"
else
    fail "C7 a cached git tree altered at the pinned commit is never consumed" \
         "an altered or added file survived into the source set"
fi

c="$(lab_copy wrong-origin)"
git -C "${c}/git/jellyfin-ffmpeg" remote set-url origin "${LAB}/somewhere-else"
run "${c}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]] \
   && [[ "$(git -C "${c}/git/jellyfin-ffmpeg" remote get-url origin)" == "${ORIGIN}" ]] \
   && [[ "$(git -C "${c}/git/jellyfin-ffmpeg" rev-parse HEAD)" == "${PINNED_COMMIT}" ]]; then
    pass "C8 a cached git tree from an undeclared origin is never consumed"
else
    fail "C8 a cached git tree from an undeclared origin is never consumed" \
         "the tree kept an origin the manifest does not declare"
fi

echo "== an incomplete or over-full cache"
c="$(lab_copy missing)"
rm -f "${c}/archives/${ARCHIVE_NAME}"
rm -rf "${c}/git/jellyfin-ffmpeg"
run "${c}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]] \
   && [[ "$(ff_sha256 "${c}/archives/${ARCHIVE_NAME}")" == "${ARCHIVE_PIN}" ]] \
   && [[ "$(git -C "${c}/git/jellyfin-ffmpeg" rev-parse HEAD)" == "${PINNED_COMMIT}" ]]; then
    pass "C9 a missing cached component is fetched and verified"
else
    fail "C9 a missing cached component is fetched and verified" "the component stayed missing"
fi

# package-runtime.sh copies ${CACHE}/archives/* into the corresponding-source
# archive and walks ${CACHE}/git/*. Anything here that the manifest does not
# name is a delivered byte, so it has to be gone before the build, not after.
c="$(lab_copy unexpected)"
printf 'payload' > "${c}/archives/definitely-not-a-component.tar.gz"
printf 'payload' > "${c}/archives/${ARCHIVE_NAME}.part"
mkdir -p "${c}/git/definitely-not-a-component"
printf 'payload' > "${c}/git/definitely-not-a-component/x"
run "${c}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]] \
   && [[ ! -e "${c}/archives/definitely-not-a-component.tar.gz" ]] \
   && [[ ! -e "${c}/archives/${ARCHIVE_NAME}.part" ]] \
   && [[ ! -e "${c}/git/definitely-not-a-component" ]] \
   && grep -q "unexpected source-cache entry" <<<"${out}"; then
    pass "C10 an unexpected cache entry cannot enter corresponding source"
else
    fail "C10 an unexpected cache entry cannot enter corresponding source" \
         "an entry the manifest never declared survived into the source set"
fi

echo "== an obsolete cache"
c="$(lab_copy obsolete)"
printf 'legacy-layout\n' > "${c}/.source-cache-format"
touch "${c}/archives/${ARCHIVE_NAME}"   # the shape an older layout might have left
run "${c}"; out="${OUT}"
if [[ "${RC}" -eq 0 ]] \
   && grep -q "no recognised format stamp" <<<"${out}" \
   && [[ "$(ff_sha256 "${c}/archives/${ARCHIVE_NAME}")" == "${ARCHIVE_PIN}" ]]; then
    pass "C11 a cache in an obsolete format is discarded, not interpreted"
else
    fail "C11 a cache in an obsolete format is discarded, not interpreted" \
         "the obsolete cache was reused"
fi

echo
echo "source-cache controls: ${PASSED} passed, ${FAILED} failed"
[[ "${FAILED}" -eq 0 ]]
