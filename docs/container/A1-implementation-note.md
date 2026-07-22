# [A1] Reproducible distributable server image — implementation note

Issue: tesserafin-project/tesserafin #87. Foundational production server image.
Deliberately excludes #88–#92 (see Deferred).

## Supported platforms
- `linux/amd64` and `linux/arm64`, from the same commit, via `docker buildx`
  (`docker-bake.hcl`, targets `server`/`amd64`/`arm64`).

## Pinned base images (immutable digests)
Both are the official .NET 10 images, Ubuntu 24.04 (noble), multi-arch
(amd64+arm64):
- SDK (build stage): `mcr.microsoft.com/dotnet/sdk@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664`
- ASP.NET runtime (final stage): `mcr.microsoft.com/dotnet/aspnet@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7`

No floating tags are the source of truth; the tags above are recorded only for
provenance. `global.json` pins the SDK to `10.0.0` (`rollForward: latestMinor`).

## ffmpeg (real jellyfin-ffmpeg, not renamed)
- Distribution: `jellyfin-ffmpeg7`, upstream `github.com/jellyfin/jellyfin-ffmpeg`
  (GPL). Pinned version **`7.1.4-3`**, noble build, per architecture.
- Fetched by immutable release URL and verified by per-arch SHA-256 during the
  build (`ffmpeg-fetch` stage, `sha256sum -c`):
  - amd64 `bbaa1a5fea4fe0a23df1bfd9050af6a4a5f7fc934ebbca997d687e528a0931a6`
  - arm64 `51128c354d27db969ed9fd0d0d0cf3124444e72625237c0c4beffee4531846f6`
- Installs to `/usr/lib/jellyfin-ffmpeg/ffmpeg` (+ `ffprobe`); its own license and
  attribution ship under `/usr/share/doc/jellyfin-ffmpeg7/`. The server is pointed
  at it explicitly with `--ffmpeg /usr/lib/jellyfin-ffmpeg/ffmpeg`.

## Non-root runtime identity
- Fixed numeric `UID:GID = 10000:10000`, user `tesserafin`, `nologin` shell, no
  home. `USER 10000:10000` in the final image. No privileged mode, no Docker
  socket, no setuid needs.

## Exposed ports
- `EXPOSE 8096` (HTTP; `NetworkConfiguration.DefaultHttpPort`). HTTPS (`8920`)
  is left unexposed until a certificate is configured. UDP discovery ports are
  intentionally not published (not required for the A1 API gate).

## Runtime paths (env prefix `TESSERAFIN_`)
Set explicitly so the container never depends on `$HOME`/XDG:
- `TESSERAFIN_CONFIG_DIR=/config` — config, `encoding.xml`, `network.xml`
- `TESSERAFIN_DATA_DIR=/data` — db, metadata, plugins, logs, backups
- `TESSERAFIN_CACHE_DIR=/cache` — image cache and `<cache>/transcodes`
- Temp: system temp (`/tmp/tesserafin`). Web client dir unused (`--nowebclient`).
- Writable volumes limited to `/config`, `/cache`, `/data` (chowned to
  10000:10000). `/media` is expected to be a read-only mount.

## Version / tag derivation
- Canonical version source: `SharedVersion.cs` → `12.0.0` (what the server logs
  at startup). No `build.yaml`/`version.json` exists; `bump_version` is stale.
- Pre-release tags (never `latest` this loop):
  - `ghcr.io/tesserafin-project/tesserafin:<version>-dev.<short-sha>`
  - `ghcr.io/tesserafin-project/tesserafin:sha-<full-sha>`

## Reproducibility method
- Deterministic publish: `-p:Deterministic=true -p:ContinuousIntegrationBuild=true`,
  `DebugType=none`, no doc XML.
- `SOURCE_DATE_EPOCH` = commit time; published file mtimes clamped to it;
  buildx `rewrite-timestamp=true` on the OCI export clamps layer timestamps; OCI
  `created` label derived from the same epoch.
- Provenance/SBOM attestations disabled (they embed wall-clock time).
- Gate: two clean, no-cache `amd64` builds of the same commit must produce the
  same image manifest digest (`docker/repro-check.sh`). Functional equivalence is
  not accepted as a substitute.

## Graceful shutdown
- The `tesserafin` apphost runs as PID 1 (exec-form entrypoint); .NET's
  `ConsoleLifetime` receives `SIGTERM` directly and triggers
  `IHostApplicationLifetime.StopApplication`, after which the host runs its
  bounded DB-shutdown task and exits. No tini needed for signal delivery.

## Image shape
- Multi-stage: SDK/build → ffmpeg-fetch → aspnet/runtime. The final image
  contains only the published app payload + ffmpeg + minimal native libs
  (`libfontconfig1`, `fonts-dejavu-core`; ICU already in the runtime base). No
  SDK, compiler, source tree, or NuGet cache.

## Deferred (out of #87 scope)
- #88 volume/permission migration policy, backup/restore (only the minimal
  boot directories are created here; that is **not** #88).
- #89 Docker Compose / NAS templates.
- #90 GPU discovery / device passthrough.
- #91 `/health` surface and logging contract.
- #92 upgrade orchestration.
- #94 hosted CI/CodeQL restoration.
- Web client: not bundled; the image boots `--nowebclient`. No paired
  tesserafin-web artifact is embedded.
