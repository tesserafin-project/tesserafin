# A6 — Image versioning, channels and the upgrade contract

Issue [#92](https://github.com/tesserafin-project/tesserafin/issues/92). This is
both the operator-facing upgrade contract and the A6 implementation note: the
audit that preceded the change is recorded in the last section.

Prerequisites: [A1](./A1-implementation-note.md) (the image),
[A2](./A2-persistent-state.md) (volumes, migrations, backup/restore),
[A3](./A3-guided-install.md) (installation).

---

## 1. Where the version comes from

There is exactly one canonical version source:

```
SharedVersion.cs  ->  [assembly: AssemblyVersion("12.0.0")]
```

An assembly version is numeric-only, so it carries the release **core**
(`MAJOR.MINOR.PATCH`) and never a SemVer pre-release suffix. A pre-release is
expressed by the git release tag (`v12.1.0-rc.1`); the contract requires the
tag's core to equal the canonical version.

Everything downstream is derived, never re-declared:

| Surface | Derived from |
|---|---|
| Image tags | `docker/version-contract.sh` |
| `org.opencontainers.image.version` | the `VERSION` build arg, from the contract |
| `org.opencontainers.image.revision` | the `VCS_REF` build arg, from the contract |
| `/health` `version` field | the running assembly version |
| `/System/Info/Public` `Version` | the running assembly version |
| `<VersionPrefix>` in every csproj | `./bump_version` |

`docker/version-verify.sh` asserts that all of them agree for a real image, and
`docker/version-contract.test.sh` asserts the tag derivation itself, including
that `docker buildx bake --print` reproduces the contract's tags byte for byte.

---

## 2. The tag policy

`docker/version-contract.sh` is the only place that derives a tag. Ask it rather
than constructing one by hand:

```console
$ docker/version-contract.sh version
12.0.0

$ docker/version-contract.sh tags --channel dev
ghcr.io/tesserafin-project/tesserafin:12.0.0-dev.<12-char commit>
ghcr.io/tesserafin-project/tesserafin:sha-<40-char commit>

$ docker/version-contract.sh check --channel stable --release-tag v12.1.0
```

### Development tags — immutable

| Tag | Mutability | Audience |
|---|---|---|
| `<version>-dev.<12-char commit>` | **immutable** | maintainers, gate runs, pre-release pinning |
| `sha-<40-char commit>` | **immutable** | automation that already knows the commit |

Both name exactly one source commit forever. They are what every pre-release
Tesserafin image published so far uses, and what the checked-in Compose file
pins. A dev build can never produce `latest`, `preview`, or a bare version tag.

### Pre-release tags

For an explicit SemVer pre-release such as `12.1.0-rc.1`:

| Tag | Mutability | Audience |
|---|---|---|
| `12.1.0-rc.1` | **immutable** | testers who want that exact release candidate |
| `preview` | **mutable** | testers who want the newest pre-release, whatever it is |
| `sha-<commit>` | **immutable** | automation |

A pre-release **never** updates `latest`. The contract refuses it, and the
release workflow asserts it separately.

### Stable release tags

For a stable release such as `12.1.0`:

| Tag | Mutability | Audience |
|---|---|---|
| `12.1.0` | **immutable** | production; the only tag suitable for pinning |
| `12.1` | **mutable** — moves on every 12.1.x patch | operators who accept automatic patch updates |
| `12` | **mutable** — moves on every 12.x release | operators who accept automatic minor updates |
| `latest` | **mutable** — moves on every stable release | evaluation and demos, not production |
| `sha-<commit>` | **immutable** | automation |

Only an explicit stable release may move `latest`.

### Compose policy

The checked-in `docker-compose.yml` and the NAS examples default to an
**immutable** reference — a digest or an exact immutable version tag. They never
default to `latest`, `preview`, a major tag or a minor tag, because a
`docker compose pull` would then silently change the running version, possibly
across a schema migration, at a moment nobody chose.

Opting into a moving channel is a deliberate, documented act:

```dotenv
# .env — opt in explicitly, and understand what you are accepting
TESSERAFIN_IMAGE=ghcr.io/tesserafin-project/tesserafin:12
```

**Channel-tag risks.** With a moving tag, `docker compose pull && docker compose
up -d` can cross a version boundary you did not plan, run forward migrations you
did not schedule, and leave you with a database the previous image can no longer
read. Nothing warns you first. If you take a channel tag, take the backup in §4
on a schedule, not on intuition.

---

## 3. Inspecting what you are actually running

Three independent surfaces, which must agree. If they ever disagree, the image
was not produced by the contract and should not be trusted.

```console
# What the image claims
$ docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}} {{index .Config.Labels "org.opencontainers.image.revision"}}' \
    ghcr.io/tesserafin-project/tesserafin:<tag>

# Which exact image is running, by digest
$ docker inspect --format '{{.Image}} {{.Config.Image}}' tesserafin
$ docker image inspect --format '{{index .RepoDigests 0}}' <image>

# What the running application reports
$ curl -fsS http://localhost:8096/health
{"status":"healthy","version":"12.0.0","database":"healthy"}
```

`/health` answers the same JSON schema from the moment the container starts. It
returns `503` with `"status":"starting"` while the server is coming up — that is
the readiness contract, not a fault. Readiness is `200` **and**
`"status":"healthy"`; a bare 200 from another endpoint is not readiness.

To check everything at once against a real container:

```console
$ docker/version-verify.sh ghcr.io/tesserafin-project/tesserafin:<tag> --require-digest
```

### Pinning to an exact version or digest

```dotenv
# .env — exact immutable version tag
TESSERAFIN_IMAGE=ghcr.io/tesserafin-project/tesserafin:12.0.0-dev.a8a18bb7c07b

# .env — digest pin, the strongest form; survives a tag being re-pointed
TESSERAFIN_IMAGE=ghcr.io/tesserafin-project/tesserafin@sha256:29493c0ecf956a61f06c2b801e7c867d560fb95ab184c09d05fdb6db508a7722
```

Resolve a tag to its digest before pinning:

```console
$ docker pull ghcr.io/tesserafin-project/tesserafin:<tag>
$ docker image inspect --format '{{index .RepoDigests 0}}' ghcr.io/tesserafin-project/tesserafin:<tag>
```

---

## 4. The upgrade contract

### What is supported

* **Forward upgrades across one minor version** (for example 12.0.x → 12.1.x)
  are supported. Skipping several minor versions in one step is not covered by
  the rehearsal; upgrade through the intervening minor versions.
* **Migrations run automatically on container replacement.** There is no manual
  migration command, no SQL to run, and no maintenance mode. The server runs its
  migration routines during startup and only then answers `/health` as healthy.
* **A backup is required before any upgrade.** Not advisory — this is the only
  supported recovery path.
* **Database downgrades are unsupported** unless a specific downgrade has been
  explicitly proven, and none has been.
* **Rolling the container back does not roll the database back.** Once the newer
  image has started and migrated, the older image may be unable to read the
  result. Recovery from a bad upgrade is *restore the pre-upgrade backup*, not
  *re-run the old tag*.
* Recovery uses the A2 backup/restore mechanism — `docker/backup.sh` and
  `docker/restore.sh`, documented in [A2](./A2-persistent-state.md).

### The procedure

```console
# 1. Back up first. This stops the server, snapshots /config and /data, restarts it.
$ docker/backup.sh --out ~/tesserafin-preupgrade.tgz \
    --config tesserafin_config --data tesserafin_data --container tesserafin

# 2. Choose the new immutable reference and record the old one.
$ docker image inspect --format '{{index .RepoDigests 0}}' <current image>   # write this down

# 3. Point .env at the new version and replace ONLY the container.
$ $EDITOR .env          # TESSERAFIN_IMAGE=...:<new immutable tag or digest>
$ docker compose pull
$ docker compose up -d  # recreates the container; the named volumes are kept

# 4. Wait for the real readiness contract, not for the container to exist.
$ until curl -fsS http://localhost:8096/health | grep -q '"status":"healthy"'; do sleep 2; done

# 5. Confirm the version actually moved, and that the migration path ran.
$ curl -fsS http://localhost:8096/health
$ docker compose logs tesserafin | grep -i "migration"
```

If step 4 never completes, do **not** start deleting volumes. Read
`docker compose logs tesserafin`, then restore the step-1 backup into fresh
volumes with `docker/restore.sh` and start the previous image against those.

`docker compose down` removes the container and keeps the named volumes;
`docker compose down -v` **deletes them**. Never use `-v` as part of an upgrade.

### What lives in each volume

| Mount | Contents | Survives an upgrade |
|---|---|---|
| `/config` | server configuration XML, user policy, plugin configuration, migration state | yes — and it is what the migration routines rewrite |
| `/data` | the SQLite databases (library, users, activity), metadata, images | yes — this is the volume a bad upgrade would damage |
| `/cache` | transcode scratch, image cache, other regenerable derivatives | yes, but losing it is harmless |
| `/media` | your library, mounted read-only | never written by the server |

Back up `/config` and `/data`. `/cache` is regenerable and is deliberately not
part of the backup archive.

### The automated rehearsal

```console
$ docker/upgrade-roundtrip.sh \
    --baseline  ghcr.io/tesserafin-project/tesserafin@sha256:<baseline digest> \
    --candidate ghcr.io/tesserafin-project/tesserafin@sha256:<candidate digest>
```

It boots the baseline by digest on fresh volumes, onboards it, creates two user
identities, a library, a visible media item, playback state and a configuration
change; replaces **only** the container; starts the candidate by digest on the
same three volumes; waits for `/health` healthy; and compares a semantic
before/after state that includes ids, not just names. It cleans up on success and
on failure unless `--keep` is passed.

It reports the number of pending migrations the runner found rather than
asserting one. `--require-migration` turns a zero count into a failure, which is
the flag to use once an honest migration boundary exists between the baseline and
the candidate.

---

## 5. Releasing

```console
# 1. Move the canonical version, verify every declaration agrees.
$ ./bump_version 12.1.0
$ ./bump_version --check

# 2. Commit, then tag. The contract refuses a tag that disagrees with the source.
$ git tag v12.1.0
$ docker/version-contract.sh verify-tag v12.1.0

# 3. Dry-run the tags the release is allowed to publish.
$ docker/version-contract.sh check --channel stable --release-tag v12.1.0

# 4. Build and push.
$ docker/build-clean.sh --target server --output push --channel stable --release-tag v12.1.0
```

The contract refuses, non-zero and before anything is built:

* a malformed canonical version;
* a git tag whose core differs from `SharedVersion.cs`;
* `latest` from a dev or pre-release build;
* missing or malformed commit provenance;
* a release from a dirty working tree, unless `--allow-dirty` is passed — which
  prints the dirty file list to stderr and records `TESSERAFIN_DIRTY_RELEASE=1`
  in the emitted environment.

`.github/workflows/release-version.yaml` runs the same assertions on a published
release and, separately, can propose the next development version as a pull
request. **Hosted GitHub Actions are parked for this repository** (#62;
restoration is tracked as #94). That workflow is correctly wired but has not been
executed on hosted infrastructure; the assertions are currently run locally.

---

## 5b. The validated A6 image

```
ghcr.io/tesserafin-project/tesserafin:12.0.0-dev.a8a18bb7c07b
```

| | Digest |
|---|---|
| Multi-arch manifest | `sha256:29493c0ecf956a61f06c2b801e7c867d560fb95ab184c09d05fdb6db508a7722` |
| `linux/amd64` | `sha256:c0b6af488905270fb18a33577e753d2a9662e9bf591fc68dd729c10f697b200d` |
| `linux/arm64` | `sha256:02b836e8d799ec3a8c4d8d332d574809415a0095216f75f26c936a2214d1e5f5` |

Built from `a8a18bb7c07b3d629ecab019aea9668a145eb5bc`. The equally immutable
`sha-a8a18bb7c07b3d629ecab019aea9668a145eb5bc` tag names the same manifest, and
`docker-compose.yml` pins the manifest digest.

Gates were run against that digest on `linux/amd64`: `docker/smoke.sh`,
`docker/observability.sh`, `docker/state-roundtrip.sh`,
`docker/browser-onboarding.sh`, `docker/version-verify.sh --require-digest`, and
`docker/upgrade-roundtrip.sh` from the A5 image
`sha256:6e3dbaab6eeaef163e81f9cc5ffb03f5a05bb9d8165e3f6487b2bb3003bc7608`.
The upgrade run reported **0 pending migrations** — see §6 for why that is the
honest outcome today.

Pull it by digest rather than by tag if you want the guarantee in writing:

```console
$ docker pull ghcr.io/tesserafin-project/tesserafin@sha256:29493c0ecf956a61f06c2b801e7c867d560fb95ab184c09d05fdb6db508a7722
```

---

## 6. Audit — the version system before A6

Recorded for review; every item below is either fixed by A6 or explicitly left
alone with a reason.

**Canonical source.** `SharedVersion.cs`. Unambiguous, and unchanged by A6.

**Duplicate / divergent version sources.**

| Location | Problem | Disposition |
|---|---|---|
| `docker/build-clean.sh` | its own `grep -oP 'AssemblyVersion…'` | now calls the contract |
| `docker/state-roundtrip.sh` | a second copy of the same grep plus its own tag string | now calls the contract |
| `docker-bake.hcl` | derived `<version>-dev.<short(VCS_REF)>` itself | derives nothing; consumes `TAGS` |
| `src/Tesserafin.Database/…Implementations.csproj` | `<VersionPrefix>10.11.0</VersionPrefix>` against a 12.0.0 source | aligned to 12.0.0 |
| `src/Tesserafin.MediaEncoding.Keyframes.csproj` | `<VersionPrefix>10.11.0</VersionPrefix>` | aligned to 12.0.0 |
| `.github/ISSUE_TEMPLATE/issue report.yml` | a hard-coded 10.11.x dropdown | left as-is; it is a Jellyfin-era support-version picker, not a version source, and rewriting it belongs to #101 |

**References to files that no longer exist.**

* `release-bump-version.yaml` read `yq e '.version' build.yaml`. There is no
  `build.yaml` in this repository, so the workflow could never complete its first
  step. Replaced.
* `bump_version` hard-coded six csproj paths, four of which
  (`MediaBrowser.Common`, `MediaBrowser.Controller`, `MediaBrowser.Model`,
  `Emby.Naming`) disappeared in the Tesserafin rename. It therefore silently
  skipped the real projects — which is how the two 10.11.0 declarations above
  survived. Rewritten to discover every csproj declaring a `<VersionPrefix>`.

**Stale Jellyfin/Reefin identity in release tooling.**

* `release-bump-version.yaml` committed as `jellyfin-bot <team@jellyfin.org>` and
  waited on a `jitterbit/await-check-suites` deploy gate this fork does not have.
  Removed with the file.
* `.github/workflows/commands.yml` still reacts to `@jellyfin-bot` comments. Left
  untouched: A6 does not touch that file, and rewriting the bot-command surface
  belongs to the identity work in #101.

**Where a disagreement was possible before A6.**

1. A git release tag could name any version; nothing compared it to
   `SharedVersion.cs`.
2. `release-bump-version.yaml` pushed a patch bump onto the release branch the
   moment a release was published — mutating the source version while that
   release's images were still being built.
3. Bake's tag interpolation and the build script's version grep were independent;
   a change to either alone produced a mislabelled image with no failure.
4. Two csproj files published NuGet packages at 10.11.0 from a 12.0.0 tree.
5. Nothing compared the OCI labels, the `/health` version and the tag of a
   produced image. `docker/version-verify.sh` now does.

**Migration boundary honesty.** The migration system (`TesserafinMigrationService`
plus the EF Core SQLite migrations) is capable of providing a real upgrade
boundary — it logs `There are N migrations for stage <Stage>` and applies them at
startup. It cannot provide one *today*: the last commit to touch either migration
tree is `7dcf867334` (2026-07-21), and every image published to GHCR was built
from a later commit. Baseline and candidate therefore share an identical
migration set, and the runner correctly reports zero pending migrations. A6
deliberately does not invent a no-op migration to colour the gate green; see the
blocker referenced from #92.
