# [A2] Volumes, permissions, migrations & backup/restore — operator note

Issue: tesserafin-project/tesserafin #88. Builds on the production image from #87
([A1]). This note defines the container's persistent-state contract and the scripts
that verify and protect it. It changes no server code and no image layer: the tooling
here (`docker/backup.sh`, `docker/restore.sh`, `docker/state-roundtrip.sh`) drives the
image that #87 already produces.

## Volume layout

The server runs with three writable volumes and one read-only media mount. The paths
are fixed by the image (`ENV TESSERAFIN_*_DIR`, `VOLUME` in the `Dockerfile`).

| Mount     | Env                     | Purpose                                             | State class            | Backed up |
|-----------|-------------------------|-----------------------------------------------------|------------------------|-----------|
| `/config` | `TESSERAFIN_CONFIG_DIR` | Server + network XML config, plugins, SSL, log dir  | Authoritative config   | **Yes**   |
| `/data`   | `TESSERAFIN_DATA_DIR`   | SQLite databases, `metadata/`, collections, playlists, scheduled-task state | Authoritative library state | **Yes** |
| `/cache`  | `TESSERAFIN_CACHE_DIR`  | Regenerable transcode/image cache                   | Disposable             | No        |
| `/media`  | —                       | Library source files, mounted **read-only** (`:ro`) | External, not server state | No     |

Notes:
- The library and user databases live under `/data` — e.g. `/data/data/tesserafin.db`
  (created on first boot, see below) and `/data/data/SQLiteBackups/`.
- Generated `metadata/` (artwork, NFO) lives under `/data`. It is regenerable from a
  library rescan but is included in the `/data` backup for convenience.
- `/cache` is intentionally **not** backed up: it holds only regenerable artefacts and
  restoring it across hosts wastes space. It is recreated on boot.
- `/media` is never part of a backup — it is the operator's own media, mounted
  read-only, and the runtime is verified to reject writes to it (see A1 smoke test).

## UID/GID and permission model

- The image declares a fixed, non-root identity **`10000:10000`** (`USER 10000:10000`;
  account `tesserafin`, `/usr/sbin/nologin`, no home). This is intentional and stable so
  that on-disk ownership is predictable across hosts and rebuilds.
- `/config`, `/cache`, `/data` are created and `chown`ed to `10000:10000` in the image,
  but a freshly-created **named volume** or **host bind mount** is owned by `root` (or
  the host user) until populated. Prepare writable volumes before first boot:

  ```sh
  # named volumes
  docker volume create tf_config tf_cache tf_data
  docker run --rm -v tf_config:/config -v tf_cache:/cache -v tf_data:/data \
    busybox chown -R 10000:10000 /config /cache /data
  ```

- `/media` stays owned by the host and is mounted `:ro`; it needs no `chown`.
- `backup.sh` archives with `--numeric-owner` and `restore.sh` re-applies `10000:10000`,
  so ownership survives a backup/restore round-trip without a `tesserafin` account on
  the host.

## Migrations (first boot and upgrade)

Migrations run automatically at startup; there is no separate migration command.
`TesserafinMigrationService.CheckFirstTimeRunOrMigration` runs during boot
(`Tesserafin.Server/Migrations/`):

- **First boot** (empty `/data`, wizard not completed): the relational database is
  created and the schema is seeded. Log lines, in order:
  `Initialise Migration service.` → `Seed migration <key>-<name>.` (per migration) →
  `Migration system initialisation completed.`
- **Every subsequent boot / upgrade** (existing `/data`): pending EF Core migrations are
  applied via `migrator.MigrateAsync(...)`; when none are pending this is a no-op and the
  server boots normally. A restored instance follows exactly this path.

This means a version upgrade needs no operator action: pull the new image tag, restart
the container on the same `/config` + `/data` volumes, and migrations apply on boot.
Take a backup first (below) so a failed upgrade can be rolled back.

## Backup and restore

Both scripts operate on the **stateful volumes** (`config` + `data`) through a throwaway
helper container, so they work with docker named volumes as well as host bind mounts and
need no host-side root. SQLite is only guaranteed consistent while the server is stopped,
so `backup.sh` stops the container around the snapshot when given `--container`.

```sh
# Back up a running server (stopped briefly for a consistent snapshot, then restarted)
docker/backup.sh --out /backups/tesserafin-$(date +%F).tgz \
  --config tf_config --data tf_data --container tesserafin

# Restore into fresh volumes (target must be empty, or pass --force)
docker volume create tf_config_new tf_data_new
docker/restore.sh --archive /backups/tesserafin-2026-07-23.tgz \
  --config tf_config_new --data tf_data_new
# then start the server on the restored volumes; migrations apply on boot
```

`backup.sh` writes the archive plus two sidecars: `<archive>.sha256` (integrity) and
`<archive>.manifest.json` (contents, expected uid/gid, source volumes). `restore.sh`
verifies the `.sha256` when present, refuses to overwrite non-empty target volumes
without `--force`, and re-asserts `10000:10000` ownership after extraction.

## Verifying the contract

`docker/state-roundtrip.sh` is an automated, source-free acceptance test for the two
measurable #88 gates. It boots the #87 image on empty volumes, asserts the schema is
created by migrations, populates an admin user + one movie library + watched playback
state through the public API, runs `backup.sh`, restores into a **second independent set
of volumes** with `restore.sh`, boots that instance, and compares the users, libraries
and playback state before vs. after.

```sh
docker/state-roundtrip.sh                       # uses the locally built dev image
docker/state-roundtrip.sh <image-ref> <port>    # or an explicit image/port
```

Expected final line: `ROUNDTRIP: all gates passed`.

## Scope and limitations

- Backup granularity is whole-volume (`config` + `data`); it is not a per-entity export
  and does not use the server's in-app backup API. It is a cold, portable filesystem
  snapshot — the most robust container-level contract.
- The round-trip fixture is a synthetic clip with no container-level runtime metadata, so
  the server records **watched-state** (`Played`, `PlayCount`, `LastPlayedDate`) rather
  than a resume `PlaybackPositionTicks`; that watched-state is the verified playback datum.
- The upgrade path is exercised as "boot on an existing, populated database" (the restored
  instance). A synthetic *cross-version* schema upgrade (old schema → newer migration) is
  not fabricated here; the applies-on-boot mechanism is the server's own and is documented
  above.
