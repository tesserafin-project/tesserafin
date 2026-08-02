# Tesserafin administrator guide

The single entry point for running a Tesserafin server after it is installed: knowing what
you are running, watching it, backing it up, upgrading it, and getting transcoding to behave.

**Installing** is a different document — follow
[`docs/container/A3-guided-install.md`](./container/A3-guided-install.md), which the
[README](../README.md) links as the primary install path.

This guide **routes**; it does not restate. Each operational area has one authoritative
document, and that document is where the exact procedure, its guarantees and its limits live.
The commands quoted here are the short ones you need at the keyboard; when a quoted command
and its authoritative document ever disagree, the document wins.

**Supported surface: the Linux container.** No Windows path semantics are claimed, because no
test in this repository runs on Windows.

| I need to… | Go to |
| --- | --- |
| install for the first time | [A3 — guided install](./container/A3-guided-install.md) |
| know exactly which build is running | [A6 §3 — inspecting what you are actually running](./container/A6-versioning-and-upgrades.md) |
| check whether the server is healthy | [A5 §1 — `GET /health`](./container/A5-observability.md) |
| read or ship the logs | [A5 §2 — structured logs](./container/A5-observability.md) |
| back up, or restore a backup | [A2 — backup and restore](./container/A2-persistent-state.md) |
| upgrade to a newer image | [A6 §4 — the upgrade contract](./container/A6-versioning-and-upgrades.md) |
| understand or fix transcoding | [A4 — hardware acceleration and the software fallback](./container/A4-hardware-acceleration.md) |
| understand volumes, UID/GID and permissions | [A2 §volume layout, §UID/GID](./container/A2-persistent-state.md) |
| supply provider credentials | [`docs/secret-configuration.md`](./secret-configuration.md), [`docs/metadata-provider-keys.md`](./metadata-provider-keys.md) |
| report a security problem | [`SECURITY.md`](../SECURITY.md) |

---

## 1. Know exactly what you are running

Do this **once, right after install**, and again after every upgrade. Write the digest down;
it is what makes a later problem report answerable.

```console
$ docker compose ps --format '{{.Image}}'
$ curl -fsS http://127.0.0.1:8096/health
```

The image should be referenced by an **immutable digest or an exact version tag**, never by a
mutable tag. `docker-compose.yml` pins a digest by default, and the project publishes no
mutable tag (`latest`, `stable`, `1`, `1.0`) at all — see
[`docs/versioning-policy.md`](./versioning-policy.md).

The version the image *claims* in its OCI labels, the version the application *reports*, and
the version in `/health` are proven equal by the version contract; A6 §1 and §3 explain how to
check each of them and what a disagreement would mean.

## 2. The daily signal: health and logs

`GET /health` is unauthenticated, cheap and side-effect-free, on the same port as everything
else:

```console
$ curl -i http://127.0.0.1:8096/health
{"status":"healthy","version":"1.0.0","database":"healthy"}
```

**200 means ready**, not merely "the process is alive". Before readiness the server answers
`503` with `"status":"starting"`, and all three fields are present from the very first
response, so a probe only ever parses one shape. The full status matrix — including
`unhealthy` with `"database":"unhealthy"` when the database stops answering — is
[A5 §1](./container/A5-observability.md).

Do **not** treat "the API answered" as "the server is up": a `200` on
`/System/Info/Public` can come from the startup server before the real host is running.
`/health` is the contract to probe.

Logs are JSON by default (`TESSERAFIN_LOG_FORMAT=json`), with the level set by
`TESSERAFIN_LOG_LEVEL`. A5 §2 documents the correlation scopes that let you follow a single
playback attempt end to end.

## 3. Back up — this is the only supported recovery path

Back up **before** you change anything, and on a schedule you actually keep. Recovery from a
bad upgrade is *restore the pre-upgrade backup*; it is not *re-run the old tag* (see §4).

```sh
docker/backup.sh --out /backups/tesserafin-$(date +%F).tgz \
  --config tf_config --data tf_data --container tesserafin
```

The script stops the container around the snapshot and restarts it, because SQLite is only
guaranteed consistent while the server is stopped. It writes the archive plus two sidecars —
`<archive>.sha256` and `<archive>.manifest.json` — and creates all three mode `0600` under
`umask 077`, **because the archive contains databases, users and access tokens**. Treat it as
a credential, not as a media file.

Restoring goes into *fresh* volumes, and the target must be empty unless you pass `--force`:

```sh
docker volume create tf_config_new tf_data_new
docker/restore.sh --archive /backups/tesserafin-2026-07-23.tgz \
  --config tf_config_new --data tf_data_new
```

Migrations apply when the server next boots on the restored volumes. The full safety contract,
each clause backed by an automated assertion in `state-roundtrip.sh`, is
[A2 §backup and restore](./container/A2-persistent-state.md).

**A backup you have never restored is a hypothesis.** Restore one into throwaway volumes once,
and confirm the server boots on it.

## 4. Upgrading

The rules that matter, before the commands:

* **A backup is required.** Not advisory — it is the only supported recovery path.
* **Upgrade through minor versions**, one at a time. Skipping several in one step is not
  covered by the rehearsal.
* **Migrations run automatically** on container replacement. There is no manual migration
  command, no SQL to run, no maintenance mode.
* **Rolling the container back does not roll the database back.** Once the newer image has
  started and migrated, the older image may be unable to read the result. Database downgrades
  are unsupported and none has been proven.
* **Never `docker compose down -v` as part of an upgrade.** `down` keeps the named volumes;
  `down -v` deletes them.

The procedure is [A6 §4](./container/A6-versioning-and-upgrades.md), in outline: back up,
record the current digest, point `.env` at the new immutable reference, `docker compose pull`
then `up -d` to replace only the container, wait for `/health` to report `healthy` — not for
the container to merely exist — then confirm the version moved and the migration path ran.

If the server never becomes healthy, **do not start deleting volumes.** Read
`docker compose logs tesserafin`, then restore the pre-upgrade backup into fresh volumes and
start the previous image against those.

## 5. Transcoding and hardware acceleration

Nothing needs configuring for a working server: with no GPU, the server transcodes in software
with zero configuration. Hardware selection is re-probed by a **real trial encode on every
start**, so a `/config` volume moved from a GPU host to a GPU-less one falls back to software
and keeps transcoding.

Every start logs exactly one conclusive decision:

```bash
docker logs tesserafin 2>&1 | grep 'Hardware acceleration decision'
```

The fields (`Mode`, `Backend`, `Reason`, `CandidatesConsidered`, `CandidatesProbed`,
`ProbeFailureCategories`) are structured logging parameters, not prose. Enabling VAAPI,
forcing software mode, the device and GID settings, the security posture of exposing a render
node, and — importantly — **which backends are actually hardware-validated** are all in
[A4](./container/A4-hardware-acceleration.md). See §7 below before assuming yours is.

## 6. Triage

| Symptom | First thing to read |
| --- | --- |
| container runs, web client unreachable | `/health`; if it answers `starting`, wait — A5 §1 |
| `/health` is `unhealthy` with `"database":"unhealthy"` | the database did not answer; `docker compose logs`, then A2 §volume layout for what lives where |
| upgrade never becomes healthy | `docker compose logs tesserafin`; then restore the pre-upgrade backup — A6 §4 |
| playback fails or stutters on one file | the hardware-acceleration decision line, then A4 |
| transcoding is slower than expected | check `Mode=` in that decision line; software is the correct answer on a host with no usable GPU |
| permission errors on `/config` or `/data` | A2 §UID/GID and permission model |
| provider metadata is missing | there is **no** built-in provider credential; configure your own — `docs/metadata-provider-keys.md` |

## 7. Limits worth knowing before you rely on this

Stated rather than omitted:

* **Only VAAPI and software transcoding are hardware-validated.** QSV, NVENC, AMF,
  VideoToolbox, RKMPP and V4L2M2M are probe-gated but not validated on real hardware.
  MJPEG VAAPI is [#76](https://github.com/tesserafin-project/tesserafin/issues/76).
* **No live mid-session retry** after a hardware transcode failure. Selection is re-probed at
  start, not mid-playback — [#119](https://github.com/tesserafin-project/tesserafin/issues/119).
* **The forward-migration boundary is unproven.** The upgrade round trip preserves users,
  libraries, media visibility, playback state and configuration, but no published image pair
  has had a pending migration between it, so *"runs forward migrations"* has not been
  demonstrated end to end — [#127](https://github.com/tesserafin-project/tesserafin/issues/127).
* **Tesserafin is not a Jellyfin drop-in.** Client, plugin and protocol compatibility is
  deliberately given up; a plugin declaring an upstream `10.x` or an inherited `12.x`
  `targetAbi` is reported as not supported.
* **The inherited `12.0.0-dev.*` images are not releases.** They are retained, unsupported
  development artifacts, and moving from one to the `1.x` line is a change of version epoch
  rather than a supported upgrade — [`docs/versioning-policy.md`](./versioning-policy.md).
