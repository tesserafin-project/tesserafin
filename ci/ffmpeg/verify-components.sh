#!/usr/bin/env bash
# Licence and source-pin gate for the Tesserafin FFmpeg runtime (F0 / #229).
#
# Usage:
#   ci/ffmpeg/verify-components.sh                     # policy only
#   ci/ffmpeg/verify-components.sh --binary PATH       # also inspect a built ffmpeg
#
# The policy lives in ci/ffmpeg/components.json and ci/ffmpeg/ffmpeg-configure.txt.
# This script is the only thing that decides whether that policy holds, so the
# build and the gate read the same two files and cannot drift apart.
#
# What it refuses to accept:
#   * a component with no licence, or a licence outside the permitted set;
#   * a component that is not pinned to a SHA-256 or a 40-character commit;
#   * an excluded component reappearing in the configure flags;
#   * --enable-nonfree or --enable-libfdk-aac anywhere in the flags;
#   * a built binary that reports nonfree/unredistributable, or that carries
#     libfdk_aac in any encoder, decoder or its build configuration.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPONENTS="${ROOT}/ci/ffmpeg/components.json"
FLAGS_FILE="${ROOT}/ci/ffmpeg/ffmpeg-configure.txt"
BINARY=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --binary)     BINARY="$2"; shift 2 ;;
        --components) COMPONENTS="$2"; shift 2 ;;
        --flags)      FLAGS_FILE="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

[[ -f "${COMPONENTS}" ]]  || { echo "missing ${COMPONENTS}" >&2; exit 2; }
[[ -f "${FLAGS_FILE}" ]]  || { echo "missing ${FLAGS_FILE}" >&2; exit 2; }

# Licences Tesserafin will redistribute inside a GPL runtime. A licence outside
# this set is not "probably fine" — it stops the build until it is classified.
PERMITTED=(
    "Apache-2.0"
    "BSD-2-Clause"
    "BSD-3-Clause"
    "BSD-3-Clause AND AOM-Patent-License-1.0"
    "FTL OR GPL-2.0-or-later"
    "GPL-2.0-or-later"
    "ISC"
    "LGPL-2.0-or-later"
    "LGPL-2.1-or-later"
    "MIT"
    "WTFPL"
    "Zlib"
)

echo "== component policy"
mapfile -t COMPONENT_LINES < <(python3 - "${COMPONENTS}" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
for c in d["components"]:
    pin = c.get("sha256") or c.get("commit") or ""
    print("\t".join([c["name"], c.get("license", ""), c.get("sourceType", ""),
                     pin, c.get("url", c.get("repository", "")), c.get("requiredBy", "")]))
PY
)

[[ "${#COMPONENT_LINES[@]}" -gt 0 ]] || fail "components.json declares no component"

declare -A SEEN_NAMES=()
for line in "${COMPONENT_LINES[@]}"; do
    IFS=$'\t' read -r name lic kind pin src why <<<"${line}"
    SEEN_NAMES["${name}"]=1

    if [[ -z "${lic}" ]]; then
        fail "${name} has no licence classification"
    else
        permitted=0
        for p in "${PERMITTED[@]}"; do [[ "${lic}" == "${p}" ]] && { permitted=1; break; }; done
        [[ "${permitted}" -eq 1 ]] || fail "${name} carries unclassified licence '${lic}'"
    fi

    case "${kind}" in
        tar)
            [[ "${pin}" =~ ^[0-9a-f]{64}$ ]] || fail "${name} has no SHA-256 pin"
            [[ "${src}" == https://* ]]      || fail "${name} source URL is not https: '${src}'"
            # A URL that names a branch or a moving tag is not a pin even when a
            # digest accompanies it, because the bytes behind it can be replaced.
            case "${src}" in
                */master*|*/main*|*/HEAD*|*latest*)
                    fail "${name} source URL points at a moving reference: ${src}" ;;
            esac
            ;;
        git)
            [[ "${pin}" =~ ^[0-9a-f]{40}$ ]] || fail "${name} is not pinned to a full commit (got '${pin}')"
            [[ "${src}" == https://* ]]      || fail "${name} repository is not https: '${src}'"
            ;;
        *) fail "${name} has unknown sourceType '${kind}'" ;;
    esac

    [[ -n "${why}" ]] || fail "${name} does not record which Tesserafin capability requires it"
done
[[ "${FAILURES}" -eq 0 ]] && pass "${#COMPONENT_LINES[@]} components: licensed, pinned and justified"

echo "== the FFmpeg baseline is pinned"
python3 - "${COMPONENTS}" <<'PY' || exit 1
import json, re, sys
f = json.load(open(sys.argv[1]))["ffmpeg"]
assert re.fullmatch(r"[0-9a-f]{40}", f["commit"]), "ffmpeg.commit is not a full commit SHA"
assert f["repository"].startswith("https://"), "ffmpeg.repository is not https"
print(f"  ok  : jellyfin-ffmpeg {f['baseline']} @ {f['commit']} ({f['commitSignature']}, {f['tagKind']} tag)")
PY

echo "== configure flags"
FLAGS="$(grep -vE '^\s*(#|$)' "${FLAGS_FILE}" || true)"
[[ -n "${FLAGS}" ]] || fail "${FLAGS_FILE} declares no flags"

if grep -qE -- '--enable-nonfree' <<<"${FLAGS}"; then
    fail "--enable-nonfree is present in ${FLAGS_FILE}"
else
    pass "no --enable-nonfree"
fi
if grep -qE -- '--enable-libfdk-aac' <<<"${FLAGS}"; then
    fail "--enable-libfdk-aac is present in ${FLAGS_FILE}"
else
    pass "no --enable-libfdk-aac"
fi
for required in -- --disable-nonfree --disable-libfdk-aac; do
    [[ "${required}" == "--" ]] && continue
    grep -qE -- "^${required}\$" <<<"${FLAGS}" \
        || fail "${FLAGS_FILE} does not state ${required} explicitly"
done
if grep -qE -- '--enable-lto' <<<"${FLAGS}"; then
    fail "--enable-lto is present; LTO is excluded because it breaks bit-for-bit reproducibility"
else
    pass "no --enable-lto"
fi

# Every --enable-lib* flag must name a component the policy actually pins, and
# no excluded component may be enabled. This is what stops the flags and the
# manifest drifting into different builds.
while read -r flag; do
    [[ "${flag}" =~ ^--enable-(lib)?([a-z0-9_]+)$ ]] || continue
    feature="${BASH_REMATCH[2]}"
    case "${feature}" in
        gpl|version3|shared|static|pic|small|cross_compile) continue ;;
    esac
    matched=0
    for name in "${!SEEN_NAMES[@]}"; do
        normalised="${name//-/}"
        [[ "${normalised}" == *"${feature}"* || "${feature}" == *"${normalised}"* ]] && { matched=1; break; }
    done
    # Header-only and kernel-interface features have no source component of the
    # same name; they are named here so the exception is visible, not implicit.
    case "${feature}" in
        vaapi|cuda|cuvid|nvdec|nvenc|ffnvcodec|amf|opencl|openssl|zlib|fontconfig) matched=1 ;;
    esac
    [[ "${matched}" -eq 1 ]] || fail "flag --enable-${BASH_REMATCH[1]}${feature} names no pinned component"
done <<<"${FLAGS}"

mapfile -t EXCLUDED < <(python3 -c '
import json,sys
for k in json.load(open(sys.argv[1]))["excluded"]:
    print(k)' "${COMPONENTS}")
for ex in "${EXCLUDED[@]}"; do
    first="${ex%% *}"
    [[ -n "${first}" ]] || continue
    if grep -qE -- "^--enable-(lib)?${first//-/[-_]}\$" <<<"${FLAGS}"; then
        fail "excluded component '${first}' is enabled in ${FLAGS_FILE}"
    fi
done
pass "no excluded component is enabled"

# --- optional: inspect a real binary -----------------------------------------
if [[ -n "${BINARY}" ]]; then
    echo "== built binary"
    [[ -x "${BINARY}" ]] || { echo "not executable: ${BINARY}" >&2; exit 2; }

    license_text="$("${BINARY}" -hide_banner -L 2>/dev/null || true)"
    buildconf="$("${BINARY}" -hide_banner -buildconf 2>/dev/null || true)"

    if grep -qiE 'nonfree|unredistributable' <<<"${license_text}"; then
        fail "the binary reports a nonfree/unredistributable licence"
    else
        pass "the binary reports a redistributable licence"
    fi
    # Match the ENABLING form only: `--disable-libfdk-aac` is the desired state
    # and contains the same substring.
    if grep -qiE -- '--enable-libfdk[-_]aac' <<<"${buildconf}"; then
        fail "the binary was configured with --enable-libfdk-aac"
    else
        pass "libfdk-aac is not enabled in the build configuration"
    fi
    if grep -qiE -- '--enable-nonfree' <<<"${buildconf}"; then
        fail "the binary was configured with --enable-nonfree"
    else
        pass "--enable-nonfree is absent from the build configuration"
    fi
    for listing in encoders decoders; do
        if "${BINARY}" -hide_banner "-${listing}" 2>/dev/null | grep -qi 'fdk'; then
            fail "libfdk_aac appears in -${listing}"
        else
            pass "no fdk codec in -${listing}"
        fi
    done
    printf '  note: %s\n' "$("${BINARY}" -hide_banner -version 2>/dev/null | head -1)"
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "COMPONENTS: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "COMPONENTS: PASS"
