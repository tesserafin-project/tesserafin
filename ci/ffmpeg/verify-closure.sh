#!/usr/bin/env bash
# Redistribution-closure gate for a PACKAGED Tesserafin FFmpeg runtime
# (F0 / #229).
#
# Usage: ci/ffmpeg/verify-closure.sh --runtime DIR --source-archive FILE
#
# verify-runtime.sh proves the binary is portable and free of forbidden
# components. This proves the artifact around it is complete enough to
# redistribute: that a recipient holding only these files can identify every
# component, read its licence and obtain its source.
#
# It refuses:
#   * a component in the manifest with no licence text and no recorded reason;
#   * a SOURCE.json that names a corresponding-source archive whose digest does
#     not match the archive actually shipped beside it;
#   * an SBOM that omits a component the manifest pins;
#   * a capability manifest asserting a hardware path as working without
#     matching physical evidence.
#
# It is not a legal opinion and does not claim to be one.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

RUNTIME=""; SRC_ARCHIVE=""; EVIDENCE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --runtime)        RUNTIME="$2"; shift 2 ;;
        --source-archive) SRC_ARCHIVE="$2"; shift 2 ;;
        --evidence)       EVIDENCE="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${RUNTIME}" ]]     || ff_die "--runtime must be an existing packaged runtime directory"
[[ -f "${SRC_ARCHIVE}" ]] || ff_die "--source-archive must be an existing corresponding-source archive"

ff_load_manifest

FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

echo "== required files"
for f in SOURCE.json THIRD_PARTY_NOTICES.md sbom.cdx.json build-configuration.txt \
         bin/ffmpeg bin/ffprobe; do
    if [[ -s "${RUNTIME}/${f}" ]]; then pass "${f}"; else fail "missing or empty ${f}"; fi
done
if [[ -d "${RUNTIME}/LICENSES" ]] && [[ -n "$(ls -A "${RUNTIME}/LICENSES" 2>/dev/null)" ]]; then
    pass "LICENSES/ carries $(find "${RUNTIME}/LICENSES" -type f | wc -l) files"
else
    fail "LICENSES/ is missing or empty"
fi

echo "== corresponding source is reachable from the artifact alone"
recorded_name="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["correspondingSource"]["archive"])' "${RUNTIME}/SOURCE.json" 2>/dev/null || true)"
recorded_sha="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["correspondingSource"]["sha256"])' "${RUNTIME}/SOURCE.json" 2>/dev/null || true)"
actual_sha="$(ff_sha256 "${SRC_ARCHIVE}")"
if [[ "${recorded_name}" == "$(basename "${SRC_ARCHIVE}")" ]]; then
    pass "SOURCE.json names the shipped archive (${recorded_name})"
else
    fail "SOURCE.json names '${recorded_name}' but the archive is '$(basename "${SRC_ARCHIVE}")'"
fi
if [[ "${recorded_sha}" == "${actual_sha}" ]]; then
    pass "the recorded corresponding-source digest matches the archive"
else
    fail "SOURCE.json records ${recorded_sha}, the archive hashes to ${actual_sha}"
fi

echo "== every pinned component is accounted for"
python3 - "${FF_COMPONENTS}" "${RUNTIME}" <<'PY' || exit 1
import json, os, sys
manifest, runtime = sys.argv[1:3]
policy = json.load(open(manifest))
source = json.load(open(os.path.join(runtime, "SOURCE.json")))
bom = json.load(open(os.path.join(runtime, "sbom.cdx.json")))
notices = open(os.path.join(runtime, "THIRD_PARTY_NOTICES.md")).read()
lic_index_path = os.path.join(runtime, "LICENSES", "index.json")
lic_index = json.load(open(lic_index_path)) if os.path.exists(lic_index_path) else {}
collected = set(lic_index.get("collected", {}))
no_text = set(lic_index.get("withoutLicenceFileInTree", []))

bom_names = {c["name"] for c in bom["components"]}
failures = 0
for c in policy["components"]:
    name = c["name"]
    if name not in bom_names:
        print(f"  FAIL: {name} is pinned but absent from the SBOM"); failures += 1
    if name not in notices:
        print(f"  FAIL: {name} is pinned but absent from THIRD_PARTY_NOTICES.md"); failures += 1
    # A component with no licence file in its tree is acceptable only if the
    # collector RECORDED that, so silence can never be mistaken for coverage.
    stem = name.split("-")[0]
    if not any(k.startswith(stem) for k in collected) and name not in no_text:
        print(f"  FAIL: {name} has neither a collected licence text nor a recorded reason")
        failures += 1
if {c["name"] for c in policy["components"]} - bom_names == set() and failures == 0:
    print(f"  ok  : {len(policy['components'])} components present in the SBOM, the notices and LICENSES/")
if source["components"] != policy["components"]:
    print("  FAIL: SOURCE.json component pins differ from ci/ffmpeg/components.json"); failures += 1
else:
    print("  ok  : SOURCE.json reproduces the pinned component set exactly")
sys.exit(1 if failures else 0)
PY
[[ $? -eq 0 ]] || FAILURES=$((FAILURES + 1))

echo "== no unsupported hardware claim"
# The runtime archive may never carry an affirmative hardware claim, not even a
# true one. Two reasons, and they point the same way:
#
#   * reproducibility — a machine with a GPU and a hosted runner without one
#     would otherwise produce different bytes for the same revision, and the
#     bit-for-bit requirement is not negotiable for a hardware accident;
#   * honesty — "works" is a property of a machine, not of an artifact. The same
#     binary on the same distribution succeeds or fails depending on a driver
#     the archive does not contain.
#
# So every path is "not runtime-tested" inside the archive, always, and runtime
# evidence lives beside it as a separate record produced by
# ci/ffmpeg/accept-hardware.sh on a machine that actually had the hardware.
# There is no free-text escape hatch here: a value that merely mentions the word
# "evidence" used to pass, which meant a sentence could claim anything.
CAP="${RUNTIME}/capability.json"
if [[ -f "${CAP}" ]]; then
    python3 - "${CAP}" <<'PY' || FAILURES=$((FAILURES + 1))
import json, sys
cap = json.load(open(sys.argv[1]))
evidence = cap.get("hardwareRuntimeEvidence", {})
paths = {k: v for k, v in evidence.items() if not k.startswith("$")}
if not paths:
    print("  FAIL: capability.json records no hardware paths at all")
    sys.exit(1)
bad = {k: v for k, v in paths.items() if v != "not runtime-tested"}
if bad:
    for k, v in bad.items():
        print(f"  FAIL: {k} is claimed as {v!r}; the archive may only say "
              f"'not runtime-tested'. Runtime evidence belongs in the separate "
              f"hardware record, not inside a reproducible artifact.")
    sys.exit(1)
print(f"  ok  : {len(paths)} hardware paths, every one marked not runtime-tested")
PY
else
    echo "  note: no capability.json in the runtime; run verify-runtime.sh --manifest first"
fi

echo "== the licence the runtime declares matches what it reports"
declared="$(python3 -c '
import json,sys
b=json.load(open(sys.argv[1]))
print(b["metadata"]["component"]["licenses"][0]["expression"])' "${RUNTIME}/sbom.cdx.json" 2>/dev/null || true)"
reported="$("${RUNTIME}/bin/ffmpeg" -hide_banner -L 2>/dev/null | head -4 | tr '\n' ' ')"
case "${declared}:${reported}" in
    "GPL-3.0-or-later:"*"version 3"*) pass "declared ${declared}, and the binary reports GPL v3" ;;
    "GPL-2.0-or-later:"*"version 2"*) pass "declared ${declared}, and the binary reports GPL v2" ;;
    *) fail "the SBOM declares '${declared}' but the binary reports: ${reported}" ;;
esac

if [[ -n "${EVIDENCE}" ]]; then
    printf '{"correspondingSource":{"archive":"%s","sha256":"%s"},"failures":%d}\n' \
        "$(basename "${SRC_ARCHIVE}")" "${actual_sha}" "${FAILURES}" > "${EVIDENCE}"
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "CLOSURE: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "CLOSURE: PASS"
