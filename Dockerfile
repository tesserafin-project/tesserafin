# syntax=docker/dockerfile:1
#
# Tesserafin server — reproducible production image (issue #87 / [A1]).
#
# Multi-stage, multi-architecture (linux/amd64, linux/arm64), non-root,
# framework-dependent .NET 10 publish over a pinned ASP.NET runtime, with the
# real jellyfin-ffmpeg build pinned by version and per-architecture checksum.
#
# Build inputs are pinned by immutable digest / checksum so two builds of the
# same commit produce the same application payload. See docker/ for the build,
# smoke and reproducibility scripts and docs/container/A1-implementation-note.md
# for the full rationale.

# ---- Pinned base images (immutable multi-arch index digests) ----
# mcr.microsoft.com/dotnet/sdk:10.0     -> Ubuntu 24.04 (noble), amd64+arm64
# mcr.microsoft.com/dotnet/aspnet:10.0  -> Ubuntu 24.04 (noble), amd64+arm64
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7

# ---- Pinned jellyfin-ffmpeg (the genuine upstream media encoder) ----
# GPL build; not renamed for branding. github.com/jellyfin/jellyfin-ffmpeg
ARG FFMPEG_VERSION=7.1.4-3
ARG FFMPEG_SHA256_AMD64=bbaa1a5fea4fe0a23df1bfd9050af6a4a5f7fc934ebbca997d687e528a0931a6
ARG FFMPEG_SHA256_ARM64=51128c354d27db969ed9fd0d0d0cf3124444e72625237c0c4beffee4531846f6

# =====================================================================
# Stage 1 — build & publish the server (native per target platform)
# =====================================================================
FROM ${SDK_IMAGE} AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

WORKDIR /src
# Copy the full pruned build context (see .dockerignore) in one layer so a
# clean checkout is the only build input; no bin/obj carried in.
COPY . .

# Deterministic, framework-dependent publish of the server host project.
# ContinuousIntegrationBuild normalises embedded paths; Deterministic removes
# non-reproducible compiler output. No RuntimeIdentifier: the emulated SDK's
# own RID yields the correct per-arch apphost ("tesserafin").
RUN dotnet restore Tesserafin.Server/Tesserafin.Server.csproj
RUN dotnet publish Tesserafin.Server/Tesserafin.Server.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        -p:Deterministic=true \
        -p:ContinuousIntegrationBuild=true \
        -p:UseAppHost=true \
        -p:DebugType=none

ARG SOURCE_DATE_EPOCH=0

# The ASP.NET static-web-assets manifest stamps each pre-compressed asset's
# `Last-Modified` header with the build-time compression clock, which is the one
# non-deterministic artefact in the publish output. Normalise every such value to
# the commit time so the manifest — and therefore the layer — is reproducible.
# (ETags are content hashes and are already stable.)
RUN set -eux; \
    fixed="$(date -u -d "@${SOURCE_DATE_EPOCH}" +'%a, %d %b %Y %H:%M:%S GMT')"; \
    f=/app/tesserafin.staticwebassets.endpoints.json; \
    if [ -f "$f" ]; then \
        sed -i -E 's/("Name":"Last-Modified","Value":")[^"]*(")/\1'"${fixed}"'\2/g' "$f"; \
    fi

# Clamp every published file's mtime to the commit time for reproducible layers.
RUN find /app -exec touch --no-dereference --date=@${SOURCE_DATE_EPOCH} {} +

# =====================================================================
# Stage 2 — fetch & verify the pinned ffmpeg .deb for the target arch
# =====================================================================
FROM ${RUNTIME_IMAGE} AS ffmpeg-fetch
ARG TARGETARCH
ARG FFMPEG_VERSION
ARG FFMPEG_SHA256_AMD64
ARG FFMPEG_SHA256_ARM64
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    case "${TARGETARCH}" in \
      amd64) sha="${FFMPEG_SHA256_AMD64}" ;; \
      arm64) sha="${FFMPEG_SHA256_ARM64}" ;; \
      *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    url="https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v${FFMPEG_VERSION}/jellyfin-ffmpeg7_${FFMPEG_VERSION}-noble_${TARGETARCH}.deb"; \
    curl -fsSL -o /tmp/ffmpeg.deb "${url}"; \
    echo "${sha}  /tmp/ffmpeg.deb" | sha256sum -c -

# =====================================================================
# Stage 3 — minimal runtime image
# =====================================================================
FROM ${RUNTIME_IMAGE} AS runtime

ARG VERSION=12.0.0
ARG VCS_REF=unknown
ARG SOURCE_DATE_EPOCH=0
ARG UID=10000
ARG GID=10000

# Runtime native dependencies:
#   - the pinned jellyfin-ffmpeg .deb (pulls its own libs)
#   - libfontconfig1 + a base font: SkiaSharp image rendering
#   - ICU is already present in the .NET runtime image (globalization on)
COPY --from=ffmpeg-fetch /tmp/ffmpeg.deb /tmp/ffmpeg.deb
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        /tmp/ffmpeg.deb \
        libfontconfig1 \
        fonts-dejavu-core \
 && rm -f /tmp/ffmpeg.deb \
 # Strip apt/dpkg artefacts that embed wall-clock timestamps, so the layer
 # content is reproducible across builds (mtimes are separately clamped by
 # buildx rewrite-timestamp).
 && rm -rf /var/lib/apt/lists/* \
           /var/cache/apt/* \
           /var/log/apt/* \
           /var/log/dpkg.log \
           /var/log/alternatives.log \
           /var/lib/dpkg/*-old \
           /var/cache/ldconfig/aux-cache

# Fixed non-root identity and the writable runtime directories.
RUN groupadd --gid ${GID} tesserafin \
 && useradd --uid ${UID} --gid ${GID} --no-create-home \
        --home-dir /data --shell /usr/sbin/nologin tesserafin \
 && mkdir -p /config /cache /data /media \
 && chown -R ${UID}:${GID} /config /cache /data

# Application payload only — no SDK, compiler, source tree or package cache.
COPY --from=build /app /opt/tesserafin
COPY LICENSE /opt/tesserafin/LICENSE

# Neutralise misleading inherited base-image env: the Tesserafin server binds its
# own port (8096) via NetworkConfiguration and ignores ASPNETCORE_HTTP_PORTS; the
# runtime identity is the fixed USER below, not the base image's APP_UID.
ENV TESSERAFIN_DATA_DIR=/data \
    TESSERAFIN_CONFIG_DIR=/config \
    TESSERAFIN_CACHE_DIR=/cache \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ASPNETCORE_HTTP_PORTS="" \
    APP_UID=""

# Real HTTP port (HTTPS 8920 stays off until a certificate is configured).
EXPOSE 8096
VOLUME ["/config", "/cache", "/data"]

# OCI provenance labels (all inputs deterministic per commit).
LABEL org.opencontainers.image.title="Tesserafin Server" \
      org.opencontainers.image.description="Reproducible distributable Tesserafin media server" \
      org.opencontainers.image.source="https://github.com/tesserafin-project/tesserafin" \
      org.opencontainers.image.url="https://github.com/tesserafin-project/tesserafin" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${VCS_REF}" \
      org.opencontainers.image.licenses="GPL-2.0-or-later" \
      org.opencontainers.image.vendor="Tesserafin"

USER ${UID}:${GID}
WORKDIR /opt/tesserafin

# The apphost runs as PID 1 so .NET's ConsoleLifetime receives SIGTERM directly
# and shuts the server down gracefully. ffmpeg is pinned by absolute path.
ENTRYPOINT ["/opt/tesserafin/tesserafin"]
CMD ["--nowebclient", "--ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg"]
