# The Tesserafin FFmpeg builder environment (F0 / #229).
#
# Two properties this image exists to have, neither of which upstream's builder
# has:
#
#   1. it is pinned. The base is a digest, and every package comes from a FIXED
#      snapshot.debian.org timestamp, so `apt-get install` resolves to the same
#      bytes today and in a year. Upstream's base is `FROM ubuntu:noble` plus
#      `apt-get dist-upgrade`, which resolves to something different every day.
#   2. its glibc is 2.31, below the Rocky Linux 9 floor of 2.34, so a binary
#      linked here runs on every declared environment.
#
# The toolchain is an INPUT. This image never builds a compiler — that is what
# makes the FFmpeg build reproducible rather than merely repeatable.
#
# clang is here for one reason: FFmpeg's CUDA filters (scale_cuda, tonemap_cuda,
# overlay_cuda, transpose_cuda) are .cu kernels that gcc cannot compile.
# --enable-cuda alone builds NVDEC/NVENC and silently omits every one of those
# filters. --enable-cuda-llvm compiles them with clang targeting NVPTX, with no
# CUDA toolkit and nothing proprietary involved.

FROM debian@sha256:99cdf7792e25416bd801861ccd8e2fb27fb527b25e8d9a8704ebc3ead2015675

# One frozen archive state. `check-valid-until=no` is required because a
# snapshot's Release file is, by construction, older than "now".
#
# Plain http on purpose: the base image ships no ca-certificates, and apt's
# integrity guarantee does not come from TLS anyway — the Release file is
# GPG-signed with the Debian archive key that the base image already carries,
# and it carries the hash of every package fetched underneath it. TLS would add
# transport privacy, not integrity, and would cost a chicken-and-egg bootstrap.
ARG DEBIAN_SNAPSHOT=20260801T000000Z

SHELL ["/bin/bash", "-euo", "pipefail", "-c"]

RUN printf '%s\n' \
      "deb [check-valid-until=no] http://snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}/ bullseye main" \
      "deb [check-valid-until=no] http://snapshot.debian.org/archive/debian-security/${DEBIAN_SNAPSHOT}/ bullseye-security main" \
      "deb [check-valid-until=no] http://snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}/ bullseye-updates main" \
      > /etc/apt/sources.list \
 && printf 'Acquire::Retries "5";\nAcquire::http::Timeout "120";\n' > /etc/apt/apt.conf.d/99tesserafin \
 && export DEBIAN_FRONTEND=noninteractive \
 && apt-get -o Acquire::Check-Valid-Until=false update \
 && apt-get install -y --no-install-recommends \
      autoconf \
      automake \
      build-essential \
      ca-certificates \
      clang \
      cmake \
      diffutils \
      file \
      gettext \
      git \
      gperf \
      libtool \
      meson \
      nasm \
      ninja-build \
      patch \
      patchelf \
      perl \
      pkg-config \
      python3 \
      python3-setuptools \
      texinfo \
      xxd \
      yasm \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

# Recorded so a build log can prove which toolchain produced an artifact.
RUN { gcc --version | head -1; clang --version | head -1; ld --version | head -1; \
      ldd --version | head -1; cmake --version | head -1; meson --version; nasm -v; } > /toolchain.txt \
 && cat /toolchain.txt

# The dependency prefix, pre-created writable so the build can run as the
# invoking user and still install into the real install location. Several
# dependencies compile this path into the binary and read from it at runtime on
# the recipient's machine, so it has to be the path the runtime is installed to
# rather than a scratch directory under /tmp — see build-in-container.sh.
RUN mkdir -p /opt/tesserafin-ffmpeg && chmod 0777 /opt/tesserafin-ffmpeg

ENV LC_ALL=C LANG=C TZ=UTC
