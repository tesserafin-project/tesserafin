#!/usr/bin/env bash
# Shared helpers for the Tesserafin FFmpeg runtime build (F0 / #229).
#
# Sourced by ci/ffmpeg/*.sh. Nothing here fetches, builds or publishes; it
# resolves the pinned inputs and provides the deterministic primitives, so
# "reproducible" has exactly one definition in this tree.

set -euo pipefail

FF_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# Overridable so a negative control can aim a gate at a doctored manifest.
# Nothing in CI sets these; if a gate could not be pointed at hostile input,
# the control would silently test the real input and always pass.
FF_COMPONENTS="${FF_COMPONENTS:-${FF_REPO_ROOT}/ci/ffmpeg/components.json}"
FF_FLAGS_FILE="${FF_FLAGS_FILE:-${FF_REPO_ROOT}/ci/ffmpeg/ffmpeg-configure.txt}"
FF_EXCLUDED_PATCHES="${FF_EXCLUDED_PATCHES:-${FF_REPO_ROOT}/ci/ffmpeg/excluded-patches.txt}"

# The builder environment, pinned by digest. Debian 11 carries glibc 2.31, which
# is below the Rocky 9 floor of 2.34, and a toolchain (gcc 10, meson 0.56,
# cmake 3.18, nasm 2.15) new enough for every pinned component. The compiler is
# an INPUT here — this build never produces its own toolchain, which is what
# makes bit-for-bit reproducibility reachable.
FF_BUILDER_IMAGE='debian@sha256:99cdf7792e25416bd801861ccd8e2fb27fb527b25e8d9a8704ebc3ead2015675'

# The highest GLIBC symbol version the produced binaries may reference.
# Measured: Rocky 9 = 2.34, Debian 12 = 2.36, Ubuntu 24.04 = 2.39, Fedora 42 = 2.41.
FF_GLIBC_FLOOR="${FF_GLIBC_FLOOR:-2.34}"

# A fixed job count, not $(nproc): parallelism must not be an input to the
# artifact, and two runners with different core counts must agree byte for byte.
FF_JOBS="${FF_JOBS:-4}"

ff_die() { echo "ffmpeg-runtime: $*" >&2; exit 1; }
ff_log() { echo "== $*" >&2; }

ff_arch_triplet() {
    case "$1" in
        linux-x64)   printf 'x86_64\n' ;;
        linux-arm64) printf 'aarch64\n' ;;
        *) ff_die "unsupported architecture: $1" ;;
    esac
}

# The build revision and the FFmpeg baseline have exactly one definition.
ff_load_manifest() {
    [[ -f "${FF_COMPONENTS}" ]] || ff_die "missing ${FF_COMPONENTS}"
    FF_BUILD_REVISION="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["buildRevision"])' "${FF_COMPONENTS}")"
    FF_FFMPEG_COMMIT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["ffmpeg"]["commit"])' "${FF_COMPONENTS}")"
    FF_FFMPEG_REPO="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["ffmpeg"]["repository"])' "${FF_COMPONENTS}")"
    FF_FFMPEG_BASELINE="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["ffmpeg"]["baseline"])' "${FF_COMPONENTS}")"
    export FF_BUILD_REVISION FF_FFMPEG_COMMIT FF_FFMPEG_REPO FF_FFMPEG_BASELINE
}

# SOURCE_DATE_EPOCH is derived from the pinned FFmpeg baseline, never from the
# clock and never from the server repository: the runtime's timestamps must not
# move when an unrelated server commit lands.
#
#   d4590e12452f94d40e413caecb34b08de608353b, committed 2026-06-06T10:56:24Z
ff_source_date_epoch() { printf '1780743384\n'; }

# One definition of "a reproducible tar of this directory".
ff_deterministic_tar() { # <dir> <dest> <compressor...>
    local dir="$1" dest="$2"; shift 2
    tar --create \
        --directory "${dir}" \
        --sort=name \
        --owner=0 --group=0 --numeric-owner \
        --mtime="@$(ff_source_date_epoch)" \
        --format=gnu \
        --exclude-vcs \
        . | "$@" > "${dest}"
}

ff_sha256() { sha256sum "$1" | cut -d' ' -f1; }

ff_clamp_mtimes() { find "$1" -exec touch --no-dereference --date="@$(ff_source_date_epoch)" {} +; }

# The highest GLIBC_2.x symbol version an ELF object references.
ff_glibc_high() { # <elf>
    readelf --dyn-syms -W "$1" 2>/dev/null \
        | grep -oE 'GLIBC_2\.[0-9]+' | sort -uV | tail -1
}

ff_version_le() { [[ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -1)" == "$1" ]]; }
