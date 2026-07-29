# [A1] Reproducible distributable server image — implementation note

> **Namespace note.** The references below name `tesserafin-project`, the
> organisation login in force when this record was written. The canonical
> organisation is now `tesserafin` and the same artifacts are served from
> `ghcr.io/tesserafin/…`. The recorded identities are preserved verbatim so this
> record keeps stating where each artifact was originally published. See the
> namespace cutover tracker, `tesserafin/tesserafin#147`.


Issue: tesserafin-project/tesserafin #87. Foundational production server image.
Deliberately excludes #88–#92 (see Deferred).

For installing this image, see the guided-install guide
[`A3-guided-install.md`](./A3-guided-install.md) (#89).

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
- Temp: system temp (`/tmp/tesserafin`).
- Web client: `/opt/tesserafin-web`, read-only application content, selected with
  `--webdir` (see "Bundled web client" below). No longer `--nowebclient`.
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
  (`libfontconfig1`, `fonts-dejavu-core`, and since #91 `curl` for the container
  `HEALTHCHECK`; ICU already in the runtime base). No SDK, compiler, source tree,
  or NuGet cache.

## Deferred (out of #87 scope)
- #88 volume/permission migration policy, backup/restore (only the minimal
  boot directories are created here; that is **not** #88).
- #89 Docker Compose / NAS templates.
- #90 GPU discovery / device passthrough.
- #91 `/health` surface and logging contract — **shipped**, see
  [`A5-observability.md`](./A5-observability.md).
- #92 upgrade orchestration.
- #94 hosted CI/CodeQL restoration.

## Bundled web client (#115 / [A1.2]) — supersedes the API-only image

The first published image (`12.0.0-dev.e2999e4e2feb`,
`sha256:0eaf26788bfb9e64213b7cc3d826c7613d71853d7276c6698ab5f49e01156182`) ran
with `--nowebclient` and bundled no web client. Because
`Tesserafin.Server/Program.cs` overrides `DefaultRedirectPath` to
`api-docs/swagger` whenever the web client is not hosted, `http://host:8096/`
served the Swagger API documentation and **no first-run onboarding wizard
existed**. That made the A3 install validation false-green: `/System/Info/Public`
answered, so the smoke passed, while the product was not installable in a
browser. Those tags are **not** deleted or overwritten; they are recorded here as
**superseded for A3**, because they cannot satisfy onboarding.

The distributable image is therefore **no longer API-only**.

- **Build input.** `ghcr.io/tesserafin-project/tesserafin-web-assets`, produced by
  `Dockerfile.web-assets` in `tesserafin-project/tesserafin-web`: a `FROM
  scratch` image containing only the production `dist` tree (`/web`), the licence
  and attribution (`/licenses`) and a deterministic revision manifest
  (`/metadata/web-revision.json`). No Node.js, no source, no build tools, no
  entrypoint. It is a build input, never a service — the user-facing deployment
  stays a single runtime container.
- **Pinned by digest, not by tag.** `ARG WEB_ASSETS_IMAGE` in the `Dockerfile`
  carries the full manifest digest; `WEB_ASSETS_TAG` is recorded for provenance
  only. The paired web commit is in `ARG WEB_VCS_REF`, in the
  `org.tesserafin.web.revision` OCI label, and in
  `/opt/tesserafin-web.revision.json` inside the image — so the pairing is
  auditable from a pulled image with no registry or label lookup.
- **Identical bytes on every architecture.** The stage is declared
  `FROM --platform=linux/amd64 ${WEB_ASSETS_IMAGE}`. Without the platform pin,
  `COPY --from=` would resolve per target platform and the amd64 and arm64 server
  images could carry different web revisions. The payload is architecture-neutral
  static content and the stage is never executed, so the pin costs no emulation.
- **Serving.** `CMD ["--webdir", "/opt/tesserafin-web", "--ffmpeg", ...]`. With
  the web client hosted, `DefaultRedirectPath` keeps its `web/` default, so `/`
  is a **302 to `/web/`** which serves `index.html` — the server's own serving
  model (`Tesserafin.Server/Startup.cs` mounts the static files at
  `RequestPath = "/web"`). The Swagger UI remains available on its own
  `/api-docs/swagger` route and no longer captures `/`.
- **Reproducibility of the pairing.** Same commit + same pinned web digest =>
  same server image digest. `docker/repro-check.sh` is unchanged and still gates
  it; the web digest is one more immutable input, like the base images and the
  ffmpeg checksums.

### Gates that replaced the false-green smoke

- `docker/smoke.sh` now fails if the final command contains `--nowebclient`, if
  it does not pass `--webdir`, if `/opt/tesserafin-web/index.html` is absent, if
  the paired-web label and the in-image manifest disagree, if a Node.js runtime
  is present, or if `/` does not resolve to an HTML web document.
- `docker/browser-onboarding.sh` + `docker/browser-gate/` drive a **real
  browser** (Playwright) against the candidate container on pristine volumes: it
  asserts the wizard is presented, creates the initial admin account through the
  UI, adds `/media` as a library, completes onboarding, and re-checks the state
  after a restart and after container recreation. Onboarding is never driven
  through the `/Startup/*` API — doing so would re-create exactly the class of
  false-green this gate exists to kill, since those calls also succeed against an
  image with no web client at all. The script begins with a negative guard that
  the previously published API-only image fails.
- `docker/browser-gate/` is a repository CI harness. It is excluded from the
  build context and is never required by an installer.
