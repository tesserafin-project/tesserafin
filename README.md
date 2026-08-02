# Tesserafin

[![License: GPL-2.0-or-later](https://img.shields.io/badge/license-GPL--2.0--or--later-blue.svg)](LICENSE)

Tesserafin is a self-hosted media system. You run the server on hardware you
control, it organises your own media library, and it streams that library to
your own devices over your own network.

Tesserafin is a fork of [Jellyfin](https://github.com/jellyfin/jellyfin), which
is itself descended from Emby's 3.5.2 release. Tesserafin is an independent
project: it does **not** claim product or protocol compatibility with Jellyfin,
it is not compatible with Jellyfin clients or plugins, and it is neither endorsed
by nor affiliated with the Jellyfin project. See [NOTICE](NOTICE) for the full
fork attribution.

## Project status

**Tesserafin has not shipped a public release yet.** Read this section before
deciding whether to install anything.

- No public Stable release exists. There is no GitHub Release in either
  repository.
- The container images that exist today are **private development and
  release-candidate artefacts**, published to GHCR for reproducibility and gate
  evidence. They are not a supported product.
- `1.0.0` is the first public version epoch. The server and the web client share
  that number.
- The inherited `12.x` server images and `13.x` web-assets images describe
  upstream lineage, not a Tesserafin release history. They are retained as
  development evidence and are **unsupported**.
- Only **Stable** is planned as a public channel. No beta, preview or nightly
  public channel is promised, and no mutable tag (`latest`, `stable`, `preview`,
  `1`, `1.0`) has been published.

The authoritative rules for which numbers exist, where they are published and how
a release may be resolved are in
[`docs/versioning-policy.md`](docs/versioning-policy.md). What the first public
release will contain, and what it will not, is in
[CHANGELOG.md](CHANGELOG.md).

## What Tesserafin is

- A **media server** that indexes your library, fetches metadata, and transcodes
  on demand — in software on any host, with hardware acceleration when a
  supported GPU is present.
- A **browser client**, Tesserafin Web, served by the same server on the same
  origin and port as the API.
- Designed for **local and private infrastructure**: a NAS, a home server, or any
  machine you administer.

Server plus browser client is the whole product today. Native mobile and TV
clients are roadmap items, not shipped software.

## Install and run

The supported way to install Tesserafin is the prebuilt container image. Nothing
needs to be built from source.

**Start here: [`docs/container/A3-guided-install.md`](docs/container/A3-guided-install.md)**
— the guided NAS / Docker operator guide, five steps from nothing to an onboarded
server.

That guide covers:

- **Docker Compose** as the canonical path, using the repository's
  [`docker-compose.yml`](docker-compose.yml) and [`.env.example`](.env.example).
  The Compose file pins its image by immutable digest, so the shipped file and the
  guide bring up the same build.
- **Persistent volumes** for `/config`, `/data` and `/cache`, created with the
  correct ownership on first boot. The volume, permission, backup and restore
  contract is [`docs/container/A2-persistent-state.md`](docs/container/A2-persistent-state.md).
- **NAS guidance**, including a host bind-mount variant, an
  [Unraid template](deployment/unraid/docker-templates/tesserafin.xml) and
  Synology DSM Container Manager steps.
- **Browser onboarding** at `http://<host-ip>:8096/`, where the server redirects
  `/` to `/web/` and serves the wizard: language, admin account, first library.
- **Hardware acceleration** is optional — see
  [`docs/container/A4-hardware-acceleration.md`](docs/container/A4-hardware-acceleration.md).
  A host with no GPU transcodes in software with zero configuration.

The canonical image package is
`ghcr.io/tesserafin-project/tesserafin-server`.

> **The GHCR packages are private.** Anonymous pulls do not work. Run
> `docker login ghcr.io` once with a GitHub token that can read packages before
> pulling. Making the packages publicly pullable is an owner decision that has not
> been taken.

Once it is installed, [`docs/user-guide.md`](docs/user-guide.md) covers first-run
onboarding, signing in, browsing a library and searching, and
[`docs/admin-guide.md`](docs/admin-guide.md) is the entry point for running it:
what you are running, health and logs, backup and restore, upgrades,
transcoding, and the limits worth knowing. Upgrades and the tag
contract are documented in full in
[`docs/container/A6-versioning-and-upgrades.md`](docs/container/A6-versioning-and-upgrades.md).
Building from source is a development activity — see [Development](#development).

## Product principles

- **The essential self-hosted server core is Free Software**, licensed
  GPL-2.0-or-later.
- **Essential server functions are not designed to sit behind a cloud
  subscription.** Indexing, browsing, transcoding and streaming your own library
  are server functions, and they stay in the server.
- **No mandatory Tesserafin-hosted service is required** to organise or stream
  your own local library. A Tesserafin install works on a network with no route
  to us.
- **Separately distributed official clients may follow their own commercial
  model.** Official mobile and TV applications, if and when they ship, are
  distributed separately from this server and are not covered by the statements
  above.

## Repository architecture

| Repository | Contents |
| --- | --- |
| [tesserafin-project/tesserafin](https://github.com/tesserafin-project/tesserafin) | This repository: the server, its container packaging, and the server-owned contracts (OpenAPI document, version contract, release-pair gate). |
| [tesserafin-project/tesserafin-web](https://github.com/tesserafin-project/tesserafin-web) | Tesserafin Web — the browser and desktop reference client, bundled into the server image. |

Native mobile and TV clients have no repository yet; they are roadmap items.

## Development

This section is for building the server from source. It is not the installation
path.

**Prerequisites**

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet).
  [`global.json`](global.json) pins SDK `10.0.0` with `rollForward: latestMinor`.
- `ffmpeg` on `PATH` for the media-encoding and playback tests. The container
  image instead pins a specific
  [jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) build by version
  and per-architecture checksum, and points the server at it with `--ffmpeg`. That
  upstream dependency name is deliberate and accurate — it is the genuine
  encoder Tesserafin ships.
- Optionally an IDE for debugging: Visual Studio 2022 or later, or Visual Studio
  Code with the workspace-recommended extensions.

Supported on all major operating systems except FreeBSD.

**Clone, build, test**

```bash
git clone https://github.com/tesserafin-project/tesserafin.git
cd tesserafin
dotnet build Tesserafin.sln
dotnet test Tesserafin.sln
```

`./ci/run.sh` is the authoritative local merge gate — it runs the full suite with
the analyzers armed. Purge `bin/` and `obj/` before trusting a pass; stale build
output can skip analyzers.

**Running the server**

The server hosts the web client's static files as well as the API. Get those
files by building
[tesserafin-web](https://github.com/tesserafin-project/tesserafin-web) (`npm
install`, then `npm run build:development`), then point the server at its `dist`
directory:

```bash
dotnet run --project Tesserafin.Server --webdir /absolute/path/to/tesserafin-web/dist
```

With no `--webdir` and no `TESSERAFIN_WEB_DIR`, the server falls back to a
`jellyfin-web` directory next to the built assembly. That inherited default path
is still live in the code and is not a branding leftover in the build.

To build once and run the executable directly:

```bash
dotnet build
cd Tesserafin.Server/bin/Debug/net10.0
./tesserafin --help          # tesserafin.exe on Windows
```

With the web client hosted, it is served at `http://localhost:8096`, and the API
documentation at `http://localhost:8096/api-docs/swagger/index.html`.

**Hosting the web client separately**

For frontend work it is usually better to run the web client from its own dev
server (`npm start` in tesserafin-web) and tell the server not to host any web
content, with the `--nowebclient` switch. A `Tesserafin.Server (nowebclient)`
launch profile is defined for that. Note that the setup wizard cannot run when
the web client is hosted separately.

**Continuous integration**

Build, test, and lint run automatically on `master` and on every pull request.
The [Tests](.github/workflows/ci-tests.yml) (build + full test suite) and
[Format](.github/workflows/ci-format.yml) (`dotnet format`) workflows, together
with the ABI, OpenAPI, dependency-audit, secret-scan, SDK-provenance, and CodeQL
workflows, are **required status checks** on `master`: a pull request cannot
merge until they are green. `./ci/run.sh` reproduces the build and test stages
locally in Docker. See [BUILDING.md](BUILDING.md) for the full contributor build
setup and the list of required checks.

## Contributing and reporting problems

For building, testing, and linting the server from source, see
[BUILDING.md](BUILDING.md) — it documents the reproducible build, the local
merge gate (`./ci/run.sh`), and the checks that must pass before a pull request
can merge. A broader contribution guide (coding conventions, review process) is
not written yet.

**Ordinary bugs and feature requests** —
[open an issue](https://github.com/tesserafin-project/tesserafin/issues) in this
repository. Browser and UI problems belong in
[tesserafin-project/tesserafin-web](https://github.com/tesserafin-project/tesserafin-web/issues).
[`docs/support.md`](docs/support.md) covers where a problem goes, what to include,
and what this project does and does not promise in return.

**Suspected security vulnerabilities — do not open a public issue or
discussion, and do not post details in a pull request.** Use the repository's
**Security** tab and select *Report a vulnerability*: that form opens a private
advisory readable only by you and the maintainers. The full policy — supported
versions, response targets, coordinated disclosure and the diagnostics posture —
is in [SECURITY.md](SECURITY.md). Vulnerability reports must not be sent to
upstream Jellyfin channels — they are not Tesserafin's.

## Licence and lineage

Tesserafin is licensed **GPL-2.0-or-later**. The bundled [LICENSE](LICENSE) file
contains the GNU General Public License version 2 text, and the same
`GPL-2.0-or-later` SPDX expression is declared by every Tesserafin-owned packable
project.

Tesserafin is a fork of Jellyfin and inherits that licensing. All prior copyright
of the Jellyfin project and its contributors is retained; this fork does not
revoke or replace any upstream attribution. Third-party components retain their
own copyright and licence notices in the source tree. Full attribution is in
[NOTICE](NOTICE).
