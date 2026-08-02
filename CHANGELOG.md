# Changelog

All notable changes to the Tesserafin server are recorded here. The browser client has its
own changelog in [`tesserafin-project/tesserafin-web`](https://github.com/tesserafin-project/tesserafin-web).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning
follows [`docs/versioning-policy.md`](./docs/versioning-policy.md), which is authoritative:
**public Tesserafin SemVer begins at `1.0.0`.**

## [Unreleased]

**Tesserafin has not published a release yet.** Nothing below has shipped to the public
Stable channel, because that channel does not exist yet. This section describes what the
first public release, `1.0.0`, will contain.

Tesserafin is a fork of [Jellyfin](https://github.com/jellyfin/jellyfin). It inherited a
`12.x` server line and a `13.x` web line from upstream history; those numbers describe a
lineage, not a Tesserafin release history. Every existing
`ghcr.io/tesserafin-project/tesserafin:12.0.0-dev.*` and
`ghcr.io/tesserafin-project/tesserafin-web-assets:13.0.0-dev.*` image is an internal,
unsupported development artifact, is retained as the reproducibility record, and is **not**
a release. Moving from one of those to the `1.x` line is a change of version epoch, not a
supported upgrade.

Because this is a first release, the entries below are stated against upstream Jellyfin
rather than against a previous Tesserafin version.

### Added

- **A distributable container image.** A production `Dockerfile`, a `docker-compose.yml`
  pinning an immutable digest by default, and an Unraid template. A clean host can
  `docker run` the image and reach the API with no source tree present.
  See [`docs/container/A1-implementation-note.md`](./docs/container/A1-implementation-note.md).
- **Persistent state with a scripted backup and restore round trip** across `/config`,
  `/data` and `/cache`, with first-boot migration.
  See [`docs/container/A2-persistent-state.md`](./docs/container/A2-persistent-state.md).
- **A guided container install** that brings up a browser-reachable instance in a documented,
  bounded number of steps.
  See [`docs/container/A3-guided-install.md`](./docs/container/A3-guided-install.md).
- **Hardware-acceleration autodetection with a guaranteed software fallback.** Selection is
  re-probed by a real trial encode on every start, so a `/config` volume moved from a GPU
  host to a GPU-less one falls back to software and keeps transcoding. VAAPI and software are
  hardware-validated.
  See [`docs/container/A4-hardware-acceleration.md`](./docs/container/A4-hardware-acceleration.md).
- **Minimal observability.** A database-aware `/health` endpoint that answers its JSON
  contract from boot (`503 status=starting` before readiness) and reports version and
  database status, plus JSON container logs with an environment-configurable level.
  See [`docs/container/A5-observability.md`](./docs/container/A5-observability.md).
- **A version contract.** Image tag, `SharedVersion.cs`, the application version, `/health`
  and the OCI image labels are proven equal from a single derivation point, and an upgrade
  round trip replaces only the container across the same volumes while preserving users,
  libraries, media visibility, playback state and configuration.
  See [`docs/container/A6-versioning-and-upgrades.md`](./docs/container/A6-versioning-and-upgrades.md).
- **A fail-closed server-to-web release-pair gate** (`ci/verify-release-pair.sh`) covering
  image provenance, bundled-web provenance across both architectures, OpenAPI equality,
  web-SDK regeneration drift, browser onboarding and repeated lifecycle rounds.
  See [`docs/container/A7-server-web-release-pair.md`](./docs/container/A7-server-web-release-pair.md).
- **A vulnerability-reporting policy.** [`SECURITY.md`](./SECURITY.md) with a coordinated
  disclosure process, response targets, and GitHub private vulnerability reporting as the
  confidential intake channel.
- **Enforced CI as required status checks on `master`**: build and full test suite, format,
  ABI compatibility, OpenAPI compatibility, dependency audit, secret scanning, cross-repository
  SDK provenance, and CodeQL with the `security-extended` query suite over C# and GitHub
  Actions.
- **Contributor and architecture documentation.** [`BUILDING.md`](./BUILDING.md) documents the
  reproducible build, the local gate and the required checks;
  [`ARCHITECTURE.md`](./ARCHITECTURE.md) documents the media pipeline, module map and the
  divergences from upstream.

### Changed

- **Identity.** Full `Jellyfin*` → `Tesserafin*` namespace and assembly cutover. Environment
  variables are `TESSERAFIN_*`.
- **Version epoch.** Public SemVer restarts at `1.0.0` for both the server and the web client
  rather than continuing the inherited `12.x`/`13.x` numbering.
- **Playback is rewritten.** Jellyfin's monolithic, DLNA-coupled `StreamBuilder` becomes a
  layered decision / engine / execution / shadow pipeline with a DLNA adapter at the edge. The
  `[Flags] TranscodeReason` bitfield is replaced by a causal reason tree that records *why* a
  method was chosen, not only which constraints were hit.
- **Hardware acceleration is rewritten.** A startup hardware-selection planner with a priority
  catalogue and trial-encode probing replaces upstream's global capability lists and inline
  selection, removing global environment-variable mutation.
- **Persistence is rewritten.** The hand-written SQLite repository is replaced by an EF Core
  context over a provider abstraction.
- **God objects are being decomposed.** `LibraryManager` and `BaseItem` are progressively split
  into narrow injected services rather than static or global access.

### Removed

- **Jellyfin client, plugin and protocol compatibility**, deliberately. A plugin declaring a
  `targetAbi` in the upstream `10.x` or the inherited `12.x` range is reported as not
  supported at `1.0.0`.
- **`DeviceProfile` from the network contract.**
- **The inherited built-in metadata provider key.** There is no built-in default; provider
  access is operator-configured. See [`docs/metadata-provider-keys.md`](./docs/metadata-provider-keys.md).

### Security

- Backup selection, manifest reading, enumeration and restore extraction now walk **every**
  path component below a managed root and refuse to traverse a symbolic link. Checking only
  the final component is insufficient, because a linked parent leaves the final component
  reporting no link target at all.
- A shared leaf-name contract is enforced at the filesystem trust boundary, and the
  operator-configured XMLTV listings path has a written authorization boundary.
  See [`docs/xmltv-listings-path.md`](./docs/xmltv-listings-path.md).
- The Schedules Direct token request body is serialized rather than concatenated, so an
  administrator-supplied listings username can no longer alter the request structure.
- Every GitHub Actions workflow declares explicit, least-privilege permissions.

### Known limitations at the first release

Stated rather than omitted:

- **No forward-migration boundary has been crossed yet.** The upgrade round trip is proven,
  but no published image pair has a pending migration between it, so *"runs forward
  migrations"* is unproven. Tracked as
  [#127](https://github.com/tesserafin-project/tesserafin/issues/127).
- **Hardware acceleration is validated for VAAPI and software only.** QSV, NVENC, AMF,
  VideoToolbox, RKMPP and V4L2M2M are probe-gated but not hardware-validated. MJPEG VAAPI is
  tracked as [#76](https://github.com/tesserafin-project/tesserafin/issues/76).
- **A live mid-session retry after a hardware transcode failure is out of scope.** Selection is
  re-probed at start, not mid-playback. Tracked as
  [#119](https://github.com/tesserafin-project/tesserafin/issues/119).
- **The CodeQL inventory is being worked through in the open**, not dismissed. Open findings
  are classified in [#185](https://github.com/tesserafin-project/tesserafin/issues/185) and
  [#188](https://github.com/tesserafin-project/tesserafin/issues/188); one finding is handled
  under coordinated disclosure and is deliberately not described publicly.
- **Only the Linux container is a supported deployment surface.** No Windows path semantics are
  claimed, because no test in this repository runs on Windows.

[Unreleased]: https://github.com/tesserafin-project/tesserafin/commits/master
