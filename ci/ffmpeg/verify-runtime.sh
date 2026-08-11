#!/usr/bin/env bash
# Portability and licence contract for a built Tesserafin FFmpeg runtime
# (F0 / #229).
#
# Usage: ci/ffmpeg/verify-runtime.sh --stage DIR --arch RID [--manifest OUT.json]
#
# Inspects the produced binaries rather than the build that made them. What it
# refuses:
#   * an RPATH or RUNPATH naming Jellyfin, a build workspace or a host path;
#   * a DT_NEEDED entry that ci/ffmpeg/allowed-dt-needed.txt does not document;
#   * an embedded build-host path;
#   * a GLIBC symbol version above the declared floor;
#   * a wrong ELF machine for the architecture;
#   * a nonfree or unredistributable licence, or any trace of libfdk_aac.
#
# With --manifest it also writes the capability record: version, buildconf and
# the full encoder/decoder/filter/protocol/hwaccel surface. That record states
# compiled capability only. It makes no runtime hardware claim, because listing
# an encoder proves it was compiled in and nothing more.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

STAGE=""; ARCH=""; MANIFEST=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --stage)    STAGE="$2"; shift 2 ;;
        --arch)     ARCH="$2"; shift 2 ;;
        --manifest) MANIFEST="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${STAGE}" ]] || ff_die "--stage must be an existing staged runtime"
[[ -n "${ARCH}" ]]  || ff_die "--arch is required"

ff_load_manifest
TRIPLET="$(ff_arch_triplet "${ARCH}")"
ALLOWED="${FF_REPO_ROOT}/ci/ffmpeg/allowed-dt-needed.txt"

FFMPEG="${STAGE}/bin/ffmpeg"
FFPROBE="${STAGE}/bin/ffprobe"
for b in "${FFMPEG}" "${FFPROBE}"; do
    [[ -x "${b}" ]] || ff_die "missing executable ${b}"
done

FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

echo "== ELF shape (${ARCH})"
for b in "${FFMPEG}" "${FFPROBE}"; do
    name="$(basename "${b}")"
    machine="$(readelf -h "${b}" | awk -F: '/Machine:/{gsub(/^ +/,"",$2); print $2}')"
    case "${TRIPLET}:${machine}" in
        x86_64:*X86-64*|aarch64:*AArch64*) pass "${name} is ${machine}" ;;
        *) fail "${name} reports machine '${machine}', expected ${TRIPLET}" ;;
    esac
done

echo "== RPATH / RUNPATH"
for b in "${FFMPEG}" "${FFPROBE}"; do
    name="$(basename "${b}")"
    rp="$(readelf -d "${b}" | grep -E '\(RPATH\)|\(RUNPATH\)' || true)"
    if [[ -z "${rp}" ]]; then
        pass "${name} carries no RPATH and no RUNPATH"
    else
        # The ONLY acceptable RUNPATH is the exact bundle-relative one. Matching
        # loosely — "not absolute, no vendor name" — once accepted a mangled
        # `25ORIGIN/../lib` produced by configure's $-handling, and that binary
        # could not load its bundled libva at all. An exact match is the only
        # form that cannot pass while being broken.
        value="$(sed -E 's/.*\[(.*)\].*/\1/' <<<"${rp}")"
        if [[ "${value}" == '$ORIGIN/../lib' ]]; then
            pass "${name} RUNPATH is exactly \$ORIGIN/../lib"
        else
            fail "${name} RUNPATH is '${value}', expected exactly \$ORIGIN/../lib"
        fi
    fi
done

echo "== DT_NEEDED closure"
mapfile -t ALLOWED_SONAMES < <(grep -vE '^\s*(#|$)' "${ALLOWED}" | awk '{print $1}')
undocumented=0
for b in "${FFMPEG}" "${FFPROBE}"; do
    name="$(basename "${b}")"
    while read -r soname; do
        [[ -n "${soname}" ]] || continue
        ok=0
        for a in "${ALLOWED_SONAMES[@]}"; do [[ "${soname}" == "${a}" ]] && { ok=1; break; }; done
        if [[ "${ok}" -ne 1 ]]; then
            undocumented=$((undocumented + 1))
            fail "${name} requires undocumented ${soname}"
        fi
    done < <(readelf -d "${b}" | grep NEEDED | sed -E 's/.*\[(.*)\].*/\1/')
done
# Counted separately from FAILURES: an earlier section failing must not make
# this one report success by omission.
[[ "${undocumented}" -eq 0 ]] && pass "every DT_NEEDED entry is documented in $(basename "${ALLOWED}")"
echo "     ffmpeg needs: $(readelf -d "${FFMPEG}" | grep NEEDED | sed -E 's/.*\[(.*)\].*/\1/' | tr '\n' ' ')"

echo "== bundled libraries exist behind that RUNPATH"
bundled=0
while read -r soname; do
    [[ -n "${soname}" ]] || continue
    case "${soname}" in
        libva.so.2|libva-drm.so.2)
            bundled=$((bundled + 1))
            if [[ -e "${STAGE}/lib/${soname}" ]]; then
                pass "${soname} is bundled at lib/${soname}"
            else
                fail "${soname} is required but not bundled; the runtime would take the host's"
            fi
            ;;
    esac
done < <(readelf -d "${FFMPEG}" | grep NEEDED | sed -E 's/.*\[(.*)\].*/\1/')
[[ "${bundled}" -gt 0 ]] || pass "no bundled shared library is required"

echo "== embedded build paths"
# -ffile-prefix-map is applied to every compilation unit, but it only reaches
# what the compiler sees. A generated config header, a build system that bakes
# its own workspace into a string, or a dependency that ignores CFLAGS can still
# leave the build machine's layout inside the shipped binary. That is both a
# reproducibility hazard — two runners with different paths would differ — and
# an information leak. The scan reads the binaries, not the build logs.
#
# --prefix is deliberately NOT in this list: /opt/tesserafin-ffmpeg is the
# installed location, it is identical on every machine, and -buildconf is
# supposed to report it.
FORBIDDEN_PATHS=(
    /tmp/tf-ffbuild   # the dependency build root
    /tmp/tf-ffdeps    # the former dependency prefix; must never come back
    /tmp/tf-ffinstall # the FFmpeg DESTDIR
    /tmp/             # any scratch path at all: several dependencies READ their
                      # compiled-in prefix at runtime, and a world-writable one
                      # is a dlopen and a config-injection surface
    /cache/           # the read-only source cache mount
    /repo/            # the read-only repository mount
    /out/             # the output mount
    /home/            # any workstation home directory
    /Users/           # ditto, on a developer machine
    /builds/          # common CI checkout roots
    /github/
    /runner/
)
for b in "${FFMPEG}" "${FFPROBE}"; do
    name="$(basename "${b}")"
    hits=()
    for p in "${FORBIDDEN_PATHS[@]}"; do
        if LC_ALL=C grep -qaF -- "${p}" "${b}"; then hits+=("${p}"); fi
    done
    if [[ "${#hits[@]}" -eq 0 ]]; then
        pass "${name} embeds no build-host path"
    else
        fail "${name} embeds build path(s): ${hits[*]}"
    fi
done

echo "== GLIBC floor (${FF_GLIBC_FLOOR}, set by Rocky Linux 9)"
for b in "${FFMPEG}" "${FFPROBE}"; do
    name="$(basename "${b}")"
    high="$(ff_glibc_high "${b}")"
    high_num="${high#GLIBC_}"
    if [[ -z "${high}" ]]; then
        pass "${name} references no versioned GLIBC symbol"
    elif ff_version_le "${high_num}" "${FF_GLIBC_FLOOR}"; then
        pass "${name} highest GLIBC symbol is ${high}"
    else
        fail "${name} requires ${high}, above the ${FF_GLIBC_FLOOR} floor"
    fi
done

echo "== licence and nonfree"
lic="$("${FFMPEG}" -hide_banner -L 2>/dev/null || true)"
conf="$("${FFMPEG}" -hide_banner -buildconf 2>/dev/null || true)"
if grep -qiE 'nonfree|unredistributable' <<<"${lic}"; then
    fail "the runtime reports a nonfree/unredistributable licence"
else
    pass "the runtime reports a redistributable licence"
fi
if grep -qiE -- '--enable-libfdk[-_]aac|--enable-nonfree' <<<"${conf}"; then
    fail "the build configuration enables a forbidden component"
else
    pass "neither --enable-libfdk-aac nor --enable-nonfree is in the build configuration"
fi
for listing in encoders decoders; do
    if "${FFMPEG}" -hide_banner "-${listing}" 2>/dev/null | grep -qi 'fdk'; then
        fail "an fdk codec appears in -${listing}"
    else
        pass "no fdk codec in -${listing}"
    fi
done

echo "== hardware surface returns rather than aborting"
# This is the specific failure the upstream portable binary has: an implib
# trampoline turns a missing libva symbol into assert(0). Here libva is linked
# normally, so the listing is a pure query and must always return.
if "${FFMPEG}" -hide_banner -hwaccels >/dev/null 2>&1; then
    pass "-hwaccels returns: $("${FFMPEG}" -hide_banner -hwaccels 2>/dev/null | tail -n +2 | tr '\n' ' ')"
else
    fail "-hwaccels did not return cleanly"
fi

# --- capability manifest ------------------------------------------------------
if [[ -n "${MANIFEST}" ]]; then
    python3 - "${FFMPEG}" "${FFPROBE}" "${ARCH}" "${FF_BUILD_REVISION}" \
              "${FF_FFMPEG_BASELINE}" "${FF_FFMPEG_COMMIT}" "${MANIFEST}" <<'PY'
import json, subprocess, sys

ffmpeg, ffprobe, arch, revision, baseline, commit, dest = sys.argv[1:8]

def run(*a):
    return subprocess.run([ffmpeg, "-hide_banner", *a], capture_output=True,
                          text=True, timeout=120).stdout

def names(listing, column):
    out = []
    for line in run(listing).splitlines():
        parts = line.split()
        if len(parts) > column and not line.startswith(("---", " ---")):
            token = parts[column]
            if token.isascii() and token.replace("_", "").replace("-", "").isalnum():
                out.append(token)
    return sorted(set(out))

manifest = {
    "buildRevision": revision,
    "architecture": arch,
    "ffmpegBaseline": baseline,
    "ffmpegCommit": commit,
    "version": run("-version").splitlines()[0] if run("-version") else "",
    "buildConfiguration": [f for f in run("-buildconf").split() if f.startswith("--")],
    "hwaccels": [x for x in run("-hwaccels").splitlines()[1:] if x.strip()],
    "encoders": names("-encoders", 1),
    "decoders": names("-decoders", 1),
    "filters": names("-filters", 1),
    "protocols": [p.strip() for p in run("-protocols").splitlines()
                  if p.strip() and not p.endswith(":")],
    "hardwareRuntimeEvidence": {
        "$comment": "Compiled capability only. Listing an encoder proves it was compiled in; "
                    "it does not prove it operates. Runtime evidence is recorded separately by "
                    "ci/ffmpeg/accept-runtime.sh and only where matching physical hardware exists.",
        "vaapi": "not runtime-tested",
        "nvenc": "not runtime-tested",
        "qsv": "not runtime-tested",
        "amf": "not runtime-tested",
    },
}
with open(dest, "w") as h:
    json.dump(manifest, h, indent=2, sort_keys=True)
    h.write("\n")
print(f"  ok  : capability manifest written ({len(manifest['encoders'])} encoders, "
      f"{len(manifest['decoders'])} decoders, {len(manifest['filters'])} filters)")
PY
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "RUNTIME: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "RUNTIME: PASS — ${ARCH}"
