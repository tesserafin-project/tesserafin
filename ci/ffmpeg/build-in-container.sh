#!/usr/bin/env bash
# The actual Tesserafin FFmpeg runtime build (F0 / #229). Runs INSIDE the
# digest-pinned builder image; ci/ffmpeg/build-runtime.sh is what puts it there.
#
# Usage: ci/ffmpeg/build-in-container.sh --cache DIR --out DIR --arch RID
#
# Every component is built static into one prefix, then FFmpeg links against
# that prefix. Nothing is taken from the host distribution except the toolchain
# and glibc, which come from the pinned image and are therefore inputs.
#
# Determinism rules applied uniformly:
#   * SOURCE_DATE_EPOCH from the pinned FFmpeg baseline, never the clock;
#   * -ffile-prefix-map so no build path is embedded;
#   * -Wl,--build-id=none so no build-id varies;
#   * a FIXED job count, because parallelism must not be an input;
#   * LC_ALL=C and TZ=UTC so no locale or zone leaks into generated files.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

CACHE=""; OUT=""; ARCH=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --cache) CACHE="$2"; shift 2 ;;
        --out)   OUT="$2"; shift 2 ;;
        --arch)  ARCH="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${CACHE}" ]] || ff_die "--cache must be an existing source cache"
[[ -n "${OUT}" ]]   || ff_die "--out is required"
[[ -n "${ARCH}" ]]  || ff_die "--arch is required"

ff_load_manifest
TRIPLET="$(ff_arch_triplet "${ARCH}")"
[[ "$(uname -m)" == "${TRIPLET}" ]] \
    || ff_die "this host is $(uname -m); ${ARCH} must be built on a native ${TRIPLET} machine"

export SOURCE_DATE_EPOCH="$(ff_source_date_epoch)"
export LC_ALL=C LANG=C TZ=UTC
export PREFIX=/opt/tesserafin-ffmpeg
WORK="$(mktemp -d /tmp/ffbuild.XXXXXX)"
mkdir -p "${PREFIX}" "${OUT}"

# A single stable build root keeps -ffile-prefix-map effective and keeps any
# path that does slip through identical between two runs.
BUILDROOT=/ffbuild
rm -rf "${BUILDROOT}"; mkdir -p "${BUILDROOT}"

export CFLAGS="-O2 -g0 -fPIC -ffile-prefix-map=${BUILDROOT}=. -ffile-prefix-map=${CACHE}=."
export CXXFLAGS="${CFLAGS}"
export LDFLAGS="-Wl,--build-id=none"
export PKG_CONFIG_PATH="${PREFIX}/lib/pkgconfig:${PREFIX}/lib64/pkgconfig:${PREFIX}/share/pkgconfig"
export PATH="${PREFIX}/bin:${PATH}"
J="${FF_JOBS}"

unpack() { # <component-name> -> echoes the unpacked directory
    local name="$1" archive dir
    archive="$(find "${CACHE}/archives" -maxdepth 1 -name "${name}-*" -print -quit)"
    [[ -n "${archive}" ]] || ff_die "no fetched archive for ${name}"
    dir="${BUILDROOT}/${name}"
    rm -rf "${dir}"; mkdir -p "${dir}"
    tar --extract --file "${archive}" --directory "${dir}" --strip-components=1
    printf '%s\n' "${dir}"
}

gitsrc() { # <component-name> -> echoes a writable copy of the pinned checkout
    local name="$1" dir="${BUILDROOT}/${1}"
    rm -rf "${dir}"
    cp -a "${CACHE}/git/${name}" "${dir}"
    printf '%s\n' "${dir}"
}

step() { ff_log "building $1"; }

# =============================================================================
# base
# =============================================================================
step zlib
d="$(unpack zlib)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --static && make -j"${J}" && make install )

step expat
d="$(unpack expat)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    --without-docbook --without-examples --without-tests && make -j"${J}" && make install )

step openssl
d="$(unpack openssl)"; ( cd "${d}" && ./Configure --prefix="${PREFIX}" --libdir=lib no-shared no-tests \
    no-docs --openssldir="${PREFIX}/ssl" && make -j"${J}" && make install_sw )

# =============================================================================
# video codecs
# =============================================================================
step x264
d="$(gitsrc x264)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --enable-static --enable-pic \
    --disable-cli --disable-opencl && make -j"${J}" && make install )

step x265
d="$(unpack x265)"; ( cd "${d}/source" && cmake -S . -B build \
    -DCMAKE_INSTALL_PREFIX="${PREFIX}" -DCMAKE_BUILD_TYPE=Release \
    -DENABLE_SHARED=OFF -DENABLE_CLI=OFF -DENABLE_PIC=ON \
    && cmake --build build -j"${J}" && cmake --install build )

step svt-av1
d="$(unpack svt-av1)"; ( cd "${d}" && cmake -S . -B build \
    -DCMAKE_INSTALL_PREFIX="${PREFIX}" -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_APPS=OFF -DBUILD_TESTING=OFF -DENABLE_AVX512=ON \
    && cmake --build build -j"${J}" && cmake --install build )

step dav1d
d="$(unpack dav1d)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
    --buildtype=release --default-library=static -Denable_tools=false -Denable_tests=false \
    && ninja -C build -j"${J}" && ninja -C build install )

step libvpx
d="$(unpack libvpx)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    --enable-pic --disable-examples --disable-tools --disable-docs --disable-unit-tests \
    --enable-vp8 --enable-vp9 --disable-vp8-encoder --enable-vp9-encoder \
    && make -j"${J}" && make install )

# =============================================================================
# audio
# =============================================================================
step lame
d="$(unpack lame)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    --enable-nasm --disable-frontend --disable-decoder && make -j"${J}" && make install )

step opus
d="$(unpack opus)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    --disable-doc --disable-extra-programs && make -j"${J}" && make install )

step libogg
d="$(unpack libogg)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    && make -j"${J}" && make install )

step libvorbis
d="$(unpack libvorbis)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    --disable-docs --disable-examples --disable-oggtest && make -j"${J}" && make install )

# =============================================================================
# filters: zimg and the subtitle stack
# =============================================================================
step zimg
d="$(gitsrc zimg)"; ( cd "${d}" && git submodule update --init --recursive 2>/dev/null || true; \
    ./autogen.sh && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    && make -j"${J}" && make install )

# freetype and harfbuzz are mutually dependent. Build freetype without
# harfbuzz, build harfbuzz against it, then rebuild freetype with harfbuzz so
# libass gets the shaping-aware freetype. Two passes, both deterministic.
step "freetype (pass 1, no harfbuzz)"
d="$(unpack freetype)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    --with-harfbuzz=no --with-brotli=no --with-bzip2=no --with-png=no && make -j"${J}" && make install )

step harfbuzz
d="$(unpack harfbuzz)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
    --buildtype=release --default-library=static \
    -Dfreetype=enabled -Dglib=disabled -Dgobject=disabled -Dcairo=disabled -Dicu=disabled \
    -Dtests=disabled -Ddocs=disabled -Dbenchmark=disabled -Dutilities=disabled \
    && ninja -C build -j"${J}" && ninja -C build install )

step "freetype (pass 2, with harfbuzz)"
d="$(unpack freetype)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    --with-harfbuzz=yes --with-brotli=no --with-bzip2=no --with-png=no && make -j"${J}" && make install )

step fribidi
d="$(unpack fribidi)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
    --buildtype=release --default-library=static -Ddocs=false -Dtests=false -Dbin=false \
    && ninja -C build -j"${J}" && ninja -C build install )

step fontconfig
d="$(unpack fontconfig)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
    --buildtype=release --default-library=static -Ddoc=disabled -Dtests=disabled -Dtools=disabled \
    -Dcache-build=disabled -Dnls=disabled && ninja -C build -j"${J}" && ninja -C build install )

step libunibreak
d="$(unpack libunibreak)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    && make -j"${J}" && make install )

step libass
d="$(unpack libass)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
    --disable-require-system-font-provider && make -j"${J}" && make install )

# =============================================================================
# hardware: the VAAPI stack and header-only SDKs
# =============================================================================
# libva is pinned at the OLDEST release in the declared matrix and linked
# normally. Upstream generates an implib trampoline here; that is precisely the
# construct that turns a missing symbol into assert(0), and it is not used.
step libdrm
d="$(unpack libdrm)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
    --buildtype=release --default-library=static \
    -Dintel=disabled -Dradeon=disabled -Damdgpu=disabled -Dnouveau=disabled -Dvmwgfx=disabled \
    -Dcairo-tests=disabled -Dman-pages=disabled -Dvalgrind=disabled -Dtests=false \
    -Dlibkms=disabled 2>/dev/null \
    || meson setup build --prefix="${PREFIX}" --libdir=lib --buildtype=release \
       --default-library=static -Dcairo-tests=disabled -Dman-pages=disabled -Dtests=false; \
    ninja -C build -j"${J}" && ninja -C build install )

step libva
d="$(unpack libva)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
    --buildtype=release --default-library=static \
    -Dwith_x11=no -Dwith_glx=no -Dwith_wayland=no -Dwith_win32=no -Denable_docs=false \
    && ninja -C build -j"${J}" && ninja -C build install )

step nv-codec-headers
d="$(gitsrc nv-codec-headers)"; ( cd "${d}" && make PREFIX="${PREFIX}" install )

step libvpl
d="$(unpack libvpl)"; ( cd "${d}" && cmake -S . -B build -DCMAKE_INSTALL_PREFIX="${PREFIX}" \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF \
    -DINSTALL_EXAMPLE_CODE=OFF && cmake --build build -j"${J}" && cmake --install build )

step amf-headers
d="$(gitsrc amf-headers)"; mkdir -p "${PREFIX}/include/AMF"; cp -a "${d}/amf/public/include/." "${PREFIX}/include/AMF/"

step opencl-headers
d="$(gitsrc opencl-headers)"; mkdir -p "${PREFIX}/include/CL"; cp -a "${d}/CL/." "${PREFIX}/include/CL/"

# =============================================================================
# FFmpeg
# =============================================================================
step "FFmpeg ${FF_FFMPEG_BASELINE} (${FF_BUILD_REVISION})"
FFSRC="${BUILDROOT}/ffmpeg"
rm -rf "${FFSRC}"; cp -a "${CACHE}/git/jellyfin-ffmpeg" "${FFSRC}"

mapfile -t FLAGS < <(grep -vE '^\s*(#|$)' "${FF_FLAGS_FILE}")
# The prefix in the flag file is the INSTALL prefix of the runtime, not the
# dependency prefix; override it here so both are explicit.
FLAGS=("${FLAGS[@]/--prefix=*/--prefix=${WORK}/install}")

(
    cd "${FFSRC}"
    ./configure \
        "${FLAGS[@]}" \
        --extra-version="tesserafin.1" \
        --pkg-config-flags=--static \
        --extra-cflags="${CFLAGS} -I${PREFIX}/include" \
        --extra-cxxflags="${CXXFLAGS} -I${PREFIX}/include" \
        --extra-ldflags="${LDFLAGS} -L${PREFIX}/lib -L${PREFIX}/lib64" \
        --extra-libs="-lpthread -lm -ldl" \
        > "${OUT}/configure.log" 2>&1 || { tail -60 "${OUT}/configure.log" >&2; ff_die "FFmpeg configure failed"; }
    make -j"${J}" > "${OUT}/make.log" 2>&1 || { tail -80 "${OUT}/make.log" >&2; ff_die "FFmpeg build failed"; }
    make install >> "${OUT}/make.log" 2>&1
)

# =============================================================================
# stage the runtime
# =============================================================================
STAGE="${OUT}/tesserafin-ffmpeg-${FF_BUILD_REVISION}-${ARCH}"
rm -rf "${STAGE}"; mkdir -p "${STAGE}/bin" "${STAGE}/LICENSES"
install -m 0755 "${WORK}/install/bin/ffmpeg"  "${STAGE}/bin/ffmpeg"
install -m 0755 "${WORK}/install/bin/ffprobe" "${STAGE}/bin/ffprobe"
strip --strip-unneeded "${STAGE}/bin/ffmpeg" "${STAGE}/bin/ffprobe"

# No development headers, no static archives, no build caches: a runtime archive
# carries what the packages run and nothing else.
ff_log "staged $(du -sh "${STAGE}" | cut -f1) at ${STAGE}"
printf '%s\n' "${STAGE}"
