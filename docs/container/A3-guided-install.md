# [A3] Guided NAS / Docker install — operator guide

Issue: tesserafin-project/tesserafin #89. Depends on #87 [A1] (the prebuilt image)
and #88 [A2] (the persistent-state contract). This guide gets a first-time
self-hoster from nothing to a running, onboarded Tesserafin server **without
building anything from source**.

Related docs:
- Image (build + contents): [`A1-implementation-note.md`](./A1-implementation-note.md)
- Volumes, permissions, backup/restore: [`A2-persistent-state.md`](./A2-persistent-state.md)
- Hardware acceleration and the software fallback:
  [`A4-hardware-acceleration.md`](./A4-hardware-acceleration.md) — optional; this
  guide needs none of it, since the server transcodes in software with no
  configuration when it finds no GPU
- Image publication tracker: #113

## The image

All paths here use the immutable pre-release image published to GHCR:

```
ghcr.io/tesserafin-project/tesserafin:12.0.0-dev.9de87471b44f
```

- Multi-arch manifest digest: `sha256:2616132aec3386f9368655bad7fbde768954da7f3c502e1e806387db321d3163`
  (`linux/amd64` `sha256:ff8f745e2c7c930b609d21a073f460188c61ac9a31f187b7b037cb6faf267af9`,
  `linux/arm64` `sha256:97f00831d33effb2e03e6b35d6746a3fd2f8cb8d3c6831f431e95b3cd50837d3`).
  Built from `9de87471b44fe6fa221dc84cad35c4e6d2a7bc34`.
- Bundled Tesserafin Web `13.0.0` at commit `fa47bab7f09d635f0b79b0814ddff2a1a1108400`
  (`ghcr.io/tesserafin-project/tesserafin-web-assets@sha256:357afd28932481f6c02a521c6482dcace58b5102190e896f79c6f515fd440a5b`),
  recorded in the image's `org.tesserafin.web.revision` label and in
  `/opt/tesserafin-web.revision.json` inside the image.

- **This is a development pre-release tag. It does NOT auto-update.** To move to a
  newer build you change the tag yourself (and take a backup first — see A2).
- The GHCR package is currently **private**: run `docker login ghcr.io` once with a
  GitHub token that can read packages before pulling. (A public, login-free pull is
  a project-owner decision tracked in #113.)

> **Do not use `12.0.0-dev.e2999e4e2feb` for this guide.** That earlier image is
> API-only: it runs with `--nowebclient` and bundles no web client, so
> `http://<host>:8096/` shows the Swagger API documentation and **there is no
> onboarding wizard**. It is still published and is not deleted, but it is
> **superseded for [A3]** because it cannot satisfy a browser install. See #115
> and the "Bundled web client" section of
> [`A1-implementation-note.md`](./A1-implementation-note.md).

## Prerequisites

- A 64-bit Linux/NAS host with Docker Engine and the Docker Compose plugin
  (`docker compose version` works), or a NAS with a container manager (Unraid /
  Synology sections below).
- Somewhere to keep your media files, reachable from the host.
- `docker login ghcr.io` completed (see above).

You do **not** need: the Tesserafin source, a compiler, the .NET SDK, or Jellyfin's
installation docs.

## Install with Docker Compose (canonical path — 5 steps to onboarded)

Steps are counted honestly: each is one action or one decision.

**1. Create an install folder and save the compose file.** Put the repository's
root [`docker-compose.yml`](../../docker-compose.yml) into an empty folder, e.g.
`~/tesserafin/docker-compose.yml`. Nothing else is required in that folder.

**2. Point it at your media.** In the same folder create a `.env` file (copy
[`.env.example`](../../.env.example)) and set your library path:

```sh
TESSERAFIN_MEDIA=/absolute/path/to/your/media
```

Leave it unset to start with an empty `./media` and add real media later.

**3. Start the server.**

```sh
docker compose up -d
```

This pulls the image and boots. Fresh named volumes for `/config`, `/data` and
`/cache` are created automatically with the correct `10000:10000` ownership — no
manual `chown`.

**4. Open the web UI.** Browse to `http://<host-ip>:8096/` (or
`http://localhost:8096/` on the same machine). The server redirects `/` to
`/web/` and serves the Tesserafin Web client from the same origin and the same
port as the API. If you land on API documentation instead, you are running the
superseded API-only image — check the tag against the one above.

**5. Complete first-run onboarding.** In the browser wizard: pick your language →
create your admin username + password → add a media library (type *Movies*, folder
`/media`) → finish. You now have a running, onboarded server.

That is the whole path: **5 numbered steps** after the one-time prerequisites.

### What just happened / where state lives

| Volume | Holds | Backed up? |
|--------|-------|-----------|
| `tesserafin_config` → `/config` | config, plugins, SSL, logs | **Yes** |
| `tesserafin_data` → `/data` | databases, metadata, watch state | **Yes** |
| `tesserafin_cache` → `/cache` | regenerable cache | No |
| your media → `/media` (**read-only**) | your files | No (it's yours) |

Named volumes live under Docker's storage (`docker volume ls | grep tesserafin`).
**Back up `/config` + `/data`** with the scripted, verified round-trip in
[`A2-persistent-state.md`](./A2-persistent-state.md#backup-and-restore).

### Stop vs uninstall

```sh
docker compose stop        # pause the server; state kept
docker compose down        # remove the container + network; NAMED VOLUMES KEPT
docker compose down -v      # ALSO delete the named volumes — destroys all state
```

Use `down` (no `-v`) for a non-destructive teardown; only `down -v` (or deleting the
volumes) removes your server state. Take an A2 backup before any destructive step.

## NAS bind-mount variant (host folders instead of named volumes)

NAS users often want the state on a known share rather than in Docker's volume area.
Replace the `volumes:` block with host paths (placeholders — use your own):

```yaml
    volumes:
      - /volume1/docker/tesserafin/config:/config
      - /volume1/docker/tesserafin/data:/data
      - /volume1/docker/tesserafin/cache:/cache
      - /volume1/media:/media:ro
```

Unlike named volumes, **freshly-created host folders are not auto-owned by the
server's uid**. Create them and set ownership once before first start:

```sh
mkdir -p /volume1/docker/tesserafin/{config,data,cache}
# make the config/data/cache dirs writable by the server's non-root identity:
docker run --rm -v /volume1/docker/tesserafin:/w \
  busybox chown -R 10000:10000 /w/config /w/data /w/cache
```

`/media` stays owned by you and is mounted read-only. Bind paths that contain
spaces are supported (quote them). See A2 for the full permission model.

## Unraid

Add the template repository and install from Community Applications:

1. Docker tab → Template Repositories →
   `https://github.com/tesserafin-project/tesserafin/tree/master/deployment/unraid/docker-templates`
2. Add Container → select **Tesserafin**.
3. Confirm the mappings: WebUI port `8096`; `/config`, `/data`, `/cache` under
   `/mnt/user/appdata/tesserafin/…`; `/media` set to your library and **read-only**.
4. Apply, then open `http://<unraid-ip>:8096/` and complete onboarding (as above).

The template runs the container as `10000:10000` and points at the immutable GHCR
image. See [`deployment/unraid/docker-templates/tesserafin.xml`](../../deployment/unraid/docker-templates/tesserafin.xml).

## Synology DSM (Container Manager)

DSM 7.2+ with **Container Manager** installed.

1. **Download the image.** Container Manager → *Registry*. If your DSM registry is
   not configured for GHCR, the reliable path is SSH + `docker login ghcr.io` then
   `docker pull ghcr.io/tesserafin-project/tesserafin:12.0.0-dev.9de87471b44f`.
2. **Create folders.** In *File Station*, under a `docker` shared folder create
   `tesserafin/config`, `tesserafin/data`, `tesserafin/cache`, and note your media
   share.
3. **Set permissions.** The server runs as UID/GID `10000:10000`. Give that
   identity read/write on `config`, `data`, `cache` (via SSH:
   `chown -R 10000:10000 /volume1/docker/tesserafin/{config,data,cache}`). Your
   media share is mounted read-only and can keep its own ownership.
4. **Create the container** from the image. Enable auto-restart.
5. **Port mapping.** Map host `8096` → container `8096` (TCP).
6. **Volume mapping.** Map the three folders to `/config`, `/data`, `/cache`
   (read-write) and your media share to `/media` (**read-only**).
7. **Run**, then open `http://<synology-ip>:8096/` and complete onboarding.

Notes:
- The immutable dev tag does **not** auto-update; pull a new tag deliberately and
  back up first.
- Back up `/config` + `/data` per [`A2-persistent-state.md`](./A2-persistent-state.md).
- Only DSM steps that are standard Container Manager operations are described here;
  no untested DSM-specific integrations are claimed.

## History: why this guide was rewritten (#115)

The first version of this guide pointed at `12.0.0-dev.e2999e4e2feb` and claimed
that step 4 opens a web UI and step 5 completes onboarding in the browser. That
claim was **not true of that image**, and the validation that accepted it was
false-green:

- the image ran with `--nowebclient` and bundled no web client;
- `Tesserafin.Server/Program.cs` therefore redirected `/` to `/api-docs/swagger`;
- opening port 8096 showed API endpoint documentation, not a wizard;
- `docker/compose-smoke.sh` asserted only that `/System/Info/Public` answered —
  API reachability, which is not browser-installability.

A tester started the published container and reported exactly that. The repair is
tracked by #115: the distributable image now bundles a pinned Tesserafin Web
production build and starts with `--webdir`, and the A3 smoke was rewritten to
fail unless a real browser reaches the first-run wizard on pristine volumes and
completes onboarding. Every validation claim tied only to the API-only image was
removed rather than reworded.

## Scope / exclusions

This guide is deliberately bounded to A3. It does **not** cover: GPU / hardware
acceleration (#90), a container healthcheck (#91), upgrade-channel orchestration
(#92), TLS/reverse-proxy, or `latest`/release channels. Those are separate roadmap
items.
