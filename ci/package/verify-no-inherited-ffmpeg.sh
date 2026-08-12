#!/usr/bin/env bash
# Negative gate: the native package surface must not reintroduce the inherited
# Jellyfin portable FFmpeg (#225 / [L0]).
#
# Usage: ci/package/verify-no-inherited-ffmpeg.sh [--root DIR]
#
# The packages once carried a prebuilt binary downloaded from an upstream release
# page, pinned by two checksums, and described as "the same FFmpeg release and
# GPL terms" as the container. They now build the accepted Tesserafin runtime
# from source. Nothing enforces that except a gate, because the removed code is
# small, plausible and easy to reintroduce by copying a line back.
#
# What this refuses, anywhere on the package surface:
#   * a portable release-asset filename;
#   * a jellyfin-ffmpeg release DOWNLOAD url;
#   * either obsolete portable checksum;
#   * a FFMPEG_PORTABLE_SHA256_* pin, or a package-side FFMPEG_VERSION pin.
#
# What it deliberately does NOT refuse: the string "jellyfin-ffmpeg" or the
# upstream baseline "7.1.4-3" on their own. The accepted runtime is built FROM
# the Jellyfin fork at a pinned commit, so F0's SOURCE.json, its SBOM and its
# THIRD_PARTY_NOTICES.md all name the project and the baseline tag — and those
# files ship inside every package. Erasing honest upstream provenance to make a
# grep quiet would be the dishonest fix. The gate targets the DOWNLOAD, not the
# ancestry.
#
# The Dockerfile is out of scope by construction: the container legitimately
# installs the upstream .deb, that is a separate distribution channel, and this
# loop does not change it.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ROOT="${PKG_REPO_ROOT}"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --root) ROOT="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

# The package surface. Docker and the server tree are not part of it.
SURFACE=(
    "ci/package"
    "ci/tests/package.test.sh"
    "packaging/linux"
    ".github/workflows/linux-packages.yml"
    "docs/distribution/L0-linux-packages.md"
)

# One pattern per forbidden reintroduction, with the reason it is forbidden.
PATTERNS=(
    'portable_linux(64|arm64)-gpl'$'\t''an upstream portable release-asset filename'
    'jellyfin-ffmpeg/releases/download'$'\t''a Jellyfin release download URL'
    'cab9ff40a47e4232d231e4eb7e4e85fabfeec56c6905266bc94291fc0881f83f'$'\t''the obsolete linux-x64 portable checksum'
    '77e4b5d044ab73e1f26c9aadaa5d6014d1782500bf2c29afb3ab81f5bea98b1f'$'\t''the obsolete linux-arm64 portable checksum'
    'FFMPEG_PORTABLE_SHA256'$'\t''a portable-asset checksum pin'
    'pkg_ffmpeg_(asset|sha256)'$'\t''the removed release-asset helpers'
    '"ffmpegAsset"'$'\t''a provenance field describing an upstream release download'
)

# The ENFORCEMENT files necessarily contain the strings they forbid, because
# forbidding a string means naming it. None of them is on any build path, so none
# of them can reintroduce a runtime download.
EXEMPT=(
    "ci/package/verify-no-inherited-ffmpeg.sh"   # names its own patterns
    "ci/package/verify-provenance.sh"            # rejects the obsolete manifest fields by name
    "ci/package/repro-controls.sh"               # fabricates an obsolete v1 manifest as a control
    "ci/tests/package.test.sh"                   # asserts both behaviours with the real values
)

FAILURES=0
echo "== inherited-runtime negative gate"

for entry in "${PATTERNS[@]}"; do
    IFS=$'\t' read -r pattern reason <<<"${entry}"
    hits=""
    for path in "${SURFACE[@]}"; do
        [[ -e "${ROOT}/${path}" ]] || continue
        # --exclude-dir is unnecessary: the surface is enumerated, not walked
        # from the repository root, so nothing outside it can be matched.
        # -H: without it, grep omits the filename when the target is a single
        # FILE rather than a directory, and the enforcement-file exclusion below
        # — which matches on path — would silently fail to apply.
        found="$(grep -rHnaE -- "${pattern}" "${ROOT}/${path}" 2>/dev/null || true)"
        [[ -n "${found}" ]] && hits+="${found}"$'\n'
    done
    # The three ENFORCEMENT files necessarily contain the strings they forbid:
    # this gate names its own patterns, verify-provenance.sh rejects the obsolete
    # manifest fields by name, and the unit tests assert both behaviours by
    # feeding them the real values. Excluding those three paths — and only those
    # three — keeps every pattern at full strength everywhere a package input
    # could actually come from. None of them can introduce a runtime download:
    # none of them is on any build path.
    for exempt in "${EXEMPT[@]}"; do
        hits="$(grep -v "${exempt}" <<<"${hits}" || true)"
    done
    hits="$(grep -v '^$' <<<"${hits}" || true)"
    if [[ -n "${hits}" ]]; then
        echo "  FAIL: ${reason} is present on the package surface:" >&2
        sed 's/^/    /' <<<"${hits}" >&2
        FAILURES=$((FAILURES + 1))
    else
        echo "  ok  : no ${reason}"
    fi
done

# A package-side FFMPEG_VERSION pin is forbidden specifically because it used to
# couple the packages to the container's encoder. The Dockerfile's own ARG keeps
# its name and is untouched.
if grep -rnaE '^[[:space:]]*FFMPEG_VERSION=' "${ROOT}/ci/package" 2>/dev/null; then
    echo "  FAIL: ci/package still declares a FFMPEG_VERSION pin coupled to the container" >&2
    FAILURES=$((FAILURES + 1))
else
    echo "  ok  : no package-side FFMPEG_VERSION pin"
fi

# The positive half: the package surface must actually reach the F0 scripts.
# A gate that only forbids can be satisfied by deleting the feature.
if grep -rqa 'ci/ffmpeg/build-runtime.sh' "${ROOT}/ci/package"; then
    echo "  ok  : the package build drives ci/ffmpeg/build-runtime.sh"
else
    echo "  FAIL: no package script builds the runtime through ci/ffmpeg/build-runtime.sh" >&2
    FAILURES=$((FAILURES + 1))
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "INHERITED-RUNTIME GATE: FAIL — ${FAILURES} finding(s)" >&2
    exit 1
fi
echo "INHERITED-RUNTIME GATE: PASS"
