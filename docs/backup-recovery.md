# Backup and recovery baseline

This baseline separates the recovery paths that already exist and the evidence
needed before extending them. It is not an import assistant or a promise of
cross-platform recovery. It implements a bounded part of
[tracker #144](https://github.com/tesserafin-project/tesserafin/issues/144).

## Choose the right archive

| Path | What it captures | How it is restored | Evidence and limits |
| --- | --- | --- | --- |
| Docker cold snapshot, `docker/backup.sh` | Whole `config` and `data` volumes, including databases, configuration and state stored there. Cache and source media are excluded. | `docker/restore.sh` into empty replacement volumes, then boot the selected server image. | Existing [A2 round trip](container/A2-persistent-state.md#verifying-the-contract) compares users, libraries and watched-state through the API. It does not certify nonzero resume position, favourites or a genuine forward migration. |
| Server-managed ZIP, `BackupService` | Database tables when `Database` is enabled (default); selected configuration files and managed folders; metadata, trickplay and subtitles according to options. It is not a whole-volume snapshot. | Administrator backup API schedules a restart and restore; `--restore-archive` is the process-level recovery entry point. | Existing [backup tests](../tests/Tesserafin.Server.Implementations.Tests/FullSystemBackup) cover content-pack round trips and archive boundaries. They do not establish whole-server disaster recovery on every platform. |
| Native Linux/Windows filesystem recovery | Requires the actual configured persistent directories and service configuration. | Stop the actual server/service and use a platform-specific, independently rehearsed procedure. | [Linux paths](../packaging/linux/tesserafin.conf) and [Windows state/service rules](distribution/install-windows.md) are documented. The Docker scripts are not native backup tools; this baseline does not claim a native restore rehearsal. |

A `.tgz` from the Docker script and a server-managed `.zip` are different formats.
Do not feed one to the other's restore operation. A successful manifest listing is
not proof that the database and all required state can be recovered.

## Docker recovery procedure

Use this for the existing container baseline. Commands run from the repository
root, with Bash, Docker access, Python 3 and the host tools used by
[`backup.sh`](../docker/backup.sh). The pinned helper image must be available.
Examples use placeholder volume/container names: inspect the actual container's
mounts and deployment configuration first, rather than assuming Compose chose
these names.

1. Record the running image's immutable reference, the container name, actual
   configuration/data mounts, media mount paths and deployment files. Retain the
   Compose file and `.env` separately: a volume snapshot does not capture host-side
   deployment configuration. Record any external certificates/proxy configuration
   separately too. Keep source media on its own backup plan.
2. Choose a new archive filename in an existing private backup directory. Avoid
   overwriting an earlier recovery point. When supplying `--container`, the script
   must successfully inspect that container before any snapshot; a missing or
   uninspectable container is an error, not evidence that the server is stopped.

   ```sh
   docker/backup.sh --out /backups/tesserafin-before-change.tgz \
     --config tf_config --data tf_data --container tesserafin
   ```

   The script stops a running source around the snapshot and restarts it. Check
   its exit status and then `/health`; a failed restart is an error even when an
   archive was created. Without `--container`, the operator must stop every
   writer before invoking the script. The scripts do not discover other
   containers or processes sharing the volumes, and do not lock out a concurrent
   administrator restarting one.
3. Retain the archive, `.sha256` and `.manifest.json` together on separate storage.
   The files contain users, tokens and private configuration; the script creates
   them owner-only. The manifest describes sources and exclusions, not a restore
   success. The checksum detects changed bytes when the sidecar is trusted; it
   does not authenticate the archive's origin.
4. Restore into new, empty volumes. Create each volume separately. Keep the
   original state intact and ensure no server writes to the replacement volumes.

   ```sh
   docker volume create tf_config_recovery
   docker volume create tf_data_recovery
   docker/restore.sh --archive /backups/tesserafin-before-change.tgz \
     --config tf_config_recovery --data tf_data_recovery
   ```

   Restore checks a checksum **if present**; retain and check the sidecar rather
   than relying on the missing-checksum warning. It checks archive layout and
   resets ownership to the container identity. `--force` empties a populated
   target, including hidden files, and may stop a named running container; it
   cannot bypass an unknown container state. Fresh volumes avoid that destructive
   path. Do not treat the archive-layout checks as permission to restore an
   untrusted archive.
5. Boot the recorded image against the replacement volumes, a fresh cache and
   the same media paths, using a separate local port/name while checking it.
   Reproduce the actual deployment's mount and runtime options. Confirm
   `/health`, sign-in, user/library identity, library visibility and playback
   state. Only switch the normal deployment to recovered volumes after checking
   the data. Stop the temporary instance before reusing those volumes.

For a failed upgrade, restore the **pre-upgrade** snapshot with its recorded old
image. Starting an old image on a database already migrated by a newer one is
not a rollback. A forward upgrade that crosses a real pending migration remains
[separate acceptance #127](https://github.com/tesserafin-project/tesserafin/issues/127).
There is no new backport or release in this procedure.

## What existing evidence proves

[`docker/state-roundtrip.sh`](../docker/state-roundtrip.sh) already creates an
administrator and movie library, records watched-state, takes a cold snapshot,
removes the source container, restores into independent volumes and compares
state after boot. It also checks permissions, restart failure, archive layout,
forced cleanup, pinned helpers, JSON manifests and bind mounts with spaces.
Run it against an exact available image reference; merely finding the test in
the repository does not mean it ran against a new artifact.

The new container-preflight check runs the real backup/restore shell entry
points with a controlled Docker process boundary. It proves refusal before volume
access or output cleanup for failed, empty and malformed state observations,
including restore with `--force`. Positive controls reach the helper for a known
stopped container and stop a running backup source before snapshotting. It does
**not** simulate a database restore or certify a Docker daemon.

```sh
python3 ci/tests/backup-container-preflight.test.py
# Requires Docker and the selected image; this is a separate, heavier test:
docker/state-roundtrip.sh <immutable-image-reference> <unused-local-port>
```

## Remaining acceptance work

These are gaps in evidence, not claims that the corresponding data is known to
be lost. Create implementation issues only after a reproducible defect.

| Scenario | Required evidence before claiming it |
| --- | --- |
| Favourites and resume position | Seed a favourite and a nonzero, non-completed resume point; assert both before backup and after an independent restore, by user and item identity. The current A2 clip proves watched-state only. |
| Native Linux and Windows disaster recovery | Artifact-based stopped-service backup/restore on the selected platform, preserving persistent directories, credentials, service identity/permissions and media paths. |
| Newer database schema | Genuine published-image migration with preservation assertions under #127; an unchanged-schema round trip is insufficient. |
| Disk-loss recovery | Off-host copy available, matching artifact and deployment configuration retained, and an isolated recovery with source storage unavailable. |
| Import from another product | Separately selected source/version and identity mapping; not restoring a Tesserafin backup and not assuming Jellyfin compatibility. |

No scheduled-backup service, retention UI, recovery key, automatic rollback or
managed-publication recovery guarantee is introduced here.
