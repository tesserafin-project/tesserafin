# L0 — Native Linux server packages

Issue [#225]. This document is the artifact contract for the downloadable native
Linux server distribution: what is produced, what is inside it, what the service
does on your machine, and — just as importantly — what is *not* claimed.

Docker/OCI remains an equally supported distribution path. Nothing here changes
it. See `docs/container/` for that half.

---

## 1. Supported formats and architectures

| Format | `linux-x64` | `linux-arm64` |
| --- | --- | --- |
| Debian package `.deb` | `amd64` | `arm64` |
| RPM package `.rpm` | `x86_64` | `aarch64` |
| Portable archive `.tar.gz` | `linux-x64` | `linux-arm64` |

Both architectures are built from one source commit and are **accepted on
architecture-native hosted runners** — `ubuntu-24.04` and `ubuntu-24.04-arm`. No
architecture is declared supported on the strength of emulation or metadata
inspection.

Environments the lifecycle suite actually exercises:

| Family | Environment | Architectures |
| --- | --- | --- |
| Debian | `ubuntu:24.04`, digest-pinned, booted with systemd as PID 1 | amd64, arm64 |
| RPM | `rockylinux:9`, digest-pinned, booted with systemd as PID 1 | amd64, arm64 |
| Portable archive | `debian:12`, digest-pinned, only the documented libraries | amd64, arm64 |

Each lifecycle boots its own container so the environment is genuinely clean —
the runner is not. Neither base image ships systemd, so the scripts layer it (and
the test tooling) on top of the pinned base first. That layered image is an
acceptance environment only: it is never a build input and never reaches an
artifact.

Anything not in that table is undeclared, not implied.

---

## 2. Package name and filesystem layout

One bundled package, `tesserafin-server`, carrying the server and the web client
together. There is no separately versioned web package to drift.

| Path | Contents |
| --- | --- |
| `/usr/bin/tesserafin` | relative symlink into the payload |
| `/usr/lib/tesserafin/` | application payload (self-contained .NET build) |
| `/usr/lib/tesserafin/ffmpeg/` | the bundled `ffmpeg` and `ffprobe` |
| `/usr/share/tesserafin/web/` | bundled Tesserafin Web client |
| `/usr/share/tesserafin/web-revision.json` | web payload provenance |
| `/etc/tesserafin/` | configuration, owned by the service account |
| `/etc/tesserafin/tesserafin.conf` | the service environment file |
| `/var/lib/tesserafin/` | persistent state: database, metadata, plugins |
| `/var/cache/tesserafin/` | cache and transcoding workspace |
| `/var/log/tesserafin/` | rolling application log files |
| `/usr/lib/systemd/system/tesserafin.service` | the unit |

The portable archive carries the same application and web payload under a
relocatable prefix and **no** `/etc` content and **no** unit file. It installs
nothing; see its own `README.md`.

---

## 3. Service identity and lifecycle

* Runs as the unprivileged system user and group `tesserafin`. The acceptance
  suite reads the running process UID back and fails if it is 0.
* Started with `--webdir /usr/share/tesserafin/web`. Never `--nowebclient`.
* Started with `--ffmpeg` pointing at the bundled encoder, so the server never
  silently falls back to whatever `ffmpeg` is on `$PATH`.
* `WorkingDirectory=/usr/lib/tesserafin`, matching the container's `WORKDIR`, so
  the server resolves its own `wwwroot` the same way.
* Logs go to the journal. The application *also* keeps its rolling file sink
  under `/var/log/tesserafin`, because `logging.json` ships one; removing it
  would change runtime behavior rather than package it.
* On first install the unit is **enabled but not started**. An operator decides
  when a media server begins serving.
* On upgrade, a running service is restarted onto the new payload.

Directories are created by the maintainer scripts at mode `0750`, owned by
`tesserafin:tesserafin`. `tesserafin.conf` stays `root:tesserafin 0640`: systemd
reads it before dropping privileges.

---

## 4. Version derivation

The package version is the canonical product version and nothing else:

```
SharedVersion.cs  ->  docker/version-contract.sh version  ->  1.0.0
```

`.deb` versions are `<version>-<revision>` and `.rpm` versions are
`<version>-<release>`. The revision/release exists so the acceptance suite can
build a synthetic *higher* package from an **identical payload** and prove an
upgrade preserves configuration and state. It never encodes a new product
version, and this work introduces none.

`SOURCE_DATE_EPOCH` is the commit time from the same contract. Every timestamp in
every artifact derives from it.

---

## 5. Provenance

| Input | Value |
| --- | --- |
| Server commit | `1cca371cbaeef63a03e055eab158b8a51759f92f` |
| Tesserafin Web commit | `a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a` |
| Tesserafin Web assets image | `ghcr.io/tesserafin-project/tesserafin-web-assets@sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c` |
| Tesserafin Web payload SHA-256 | `4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f` |
| jellyfin-ffmpeg | `7.1.4-3`, portable GPL build |

The web pin has exactly one definition — the `ARG` block in the `Dockerfile` —
and `ci/package/lib.sh` reads it from there. `ci/package/pins.env` adds only what
the container path has no reason to know, and the build fails if the two
disagree about the ffmpeg version.

The build fails closed on web provenance: the commit recorded inside the payload
must equal the pin, **and** the payload's own digest must equal the pinned
digest. Either mismatch stops the build; neither is a warning.

### Artifact manifest

Every artifact gets `<artifact>.provenance.json` next to it:

```json
{
  "packageFormat": "deb",
  "packageName": "tesserafin-server",
  "packageVersion": "1.0.0",
  "architecture": "amd64",
  "runtimeIdentifier": "linux-x64",
  "serverCommit": "...",
  "webCommit": "...",
  "webPayloadSha256": "...",
  "applicationPayloadSha256": "...",
  "ffmpegVersion": "7.1.4-3",
  "ffmpegSha256": "...",
  "sourceDateEpoch": "...",
  "buildTimestamp": "...",
  "toolchain": {
    "dotnetSdk": "...", "tar": "...", "dpkgDeb": "...",
    "rpmbuild": "4.20.1", "rpmBuilderImage": "fedora@sha256:..."
  },
  "artifactFilename": "...",
  "artifactSizeBytes": 0,
  "artifactSha256": "..."
}
```

---

## 6. Docker coexistence

The two paths are siblings, not alternatives, and they differ in exactly one
deliberate way:

| | Container image | Native packages |
| --- | --- | --- |
| .NET runtime | framework-dependent, supplied by the base image | **self-contained**, carried in the payload |
| Web payload | the pinned assets image | the same pinned assets image |
| Encoder | jellyfin-ffmpeg noble `.deb` | jellyfin-ffmpeg portable build, same release |
| Identity | fixed UID 10000 in the image | system user `tesserafin` |

The runtime difference is forced: an image can supply a runtime, a `.deb` cannot
unless it depends on a vendor APT/DNF feed, which is out of scope here.

Because of it, **payload equivalence is an intra-L0 property**: the `.deb`, the
`.rpm` and the `.tar.gz` for one architecture are proven to carry identical
application and web bytes. They are deliberately *not* byte-equal to the
container's `/opt/tesserafin`, and no gate claims they are.

Existing container gates (`docker/smoke.sh`, `docker/repro-check.sh`,
`docker/hwa-smoke.sh`) are unchanged by this work. They have always been local
scripts — this repository has no container workflow and `local-ci.yml` has no
runner — so their evidence is local, as it has always been.

---

## 7. FFmpeg boundary

**Measured, not assumed.** The relevant facts:

1. The server's encoder precedence is `--ffmpeg` / environment, then
   `EncoderAppPath` in `encoding.xml`, then a bare `ffmpeg` on `$PATH`.
2. The server **refuses to start** without a usable encoder:
   `FfmpegException: Failed to find valid ffmpeg` is fatal. "Ship no encoder" is
   therefore not a shippable option — the service would never start.
3. The container installs `jellyfin-ffmpeg7_7.1.4-3-noble_<arch>.deb`. That
   binary hard-codes `RUNPATH=/usr/lib/jellyfin-ffmpeg/lib` and needs sixteen
   external sonames. It fails `GLIBC_2.38` outright on Debian 12 and Rocky 9, and
   on EL10/Fedora it needs `libx264.so.164`, `libx265.so.199` and
   `libmp3lame.so.0`, which are in no base RPM repository. It is not
   redistributable across distributions.
4. The **same upstream release** publishes
   `jellyfin-ffmpeg_7.1.4-3_portable_{linux64,linuxarm64}-gpl.tar.xz`: two
   relocatable binaries with no external dependencies. Verified to run a real
   `libx264` encode on bare `debian:12`, `ubuntu:24.04`, `rockylinux:9` and
   `fedora:42`.
5. Capability was compared between the two builds, not assumed equal. Both report
   the same hardware acceleration methods — `cuda vaapi qsv drm opencl vulkan` —
   and the same hardware encoder set: `h264/hevc/av1` across `nvenc`, `qsv`,
   `vaapi`, `amf`, plus `h264/hevc_v4l2m2m`.

So the packages bundle the portable asset of the release the container already
pins, by version and SHA-256. Same project, same version, same GPL terms, same
supply chain — a different asset of it, which is a deviation worth naming rather
than burying. No new FFmpeg distribution strategy is introduced, and no
distribution's arbitrary `ffmpeg` is depended on, silently or otherwise.

What is **not** claimed: that the bundled encoder is byte-identical to the
container's, or that any codec beyond the compared surface behaves identically.

---

## 8. Hardware transcoding

The unit ships the least privilege that is *proven* device-compatible:
`NoNewPrivileges=true`, and nothing else. Every directive that hides or relabels
device nodes — `PrivateDevices`, `DevicePolicy`, `ProtectClock` and relatives —
is deliberately absent, because each of them breaks VAAPI (`/dev/dri/renderD*`)
or NVIDIA (`/dev/nvidia*`) access. This package does not ship hardening it has
not demonstrated to be compatible.

Device *access* still needs group membership, which is a host decision:

```sh
usermod -aG render,video tesserafin
systemctl restart tesserafin
```

The hosted lifecycle jobs have no GPU. They prove the unit does not block device
access and that the software path works; they do **not** prove hardware encoding
on any specific GPU, and this document does not claim they do. The startup
hardware decision is logged — see `docs/container/A4-hardware-acceleration.md`,
whose probe logic is the same code.

---

## 9. Uninstall and state retention

Maintainer scripts never delete data. Not on `remove`, not on `purge`,
not on `rpm -e`:

| Path | Ordinary uninstall | Debian `purge` |
| --- | --- | --- |
| `/usr/lib/tesserafin`, `/usr/share/tesserafin`, `/usr/bin/tesserafin`, the unit | removed | removed |
| `/var/lib/tesserafin` | **kept** | **kept** |
| `/etc/tesserafin` and its contents | **kept** | **kept** |
| `/var/log/tesserafin` | **kept** | **kept** |
| the `tesserafin` user and group | **kept** | **kept** |

Package management removes what the package installed. Everything the server
produced — the database, metadata, user images, and anything an operator put
beside it — belongs to the operator, and only the operator deletes it. The user
is kept too, so nothing left behind becomes orphaned.

Upgrades preserve `/etc/tesserafin/tesserafin.conf` through the ordinary
mechanisms: a dpkg conffile and an rpm `%config(noreplace)`. The acceptance suite
writes sentinels into configuration and state, upgrades, and reads them back.

---

## 10. Reproducibility

Two independent clean builds of every artifact must produce identical SHA-256
digests. The gate compares digests; it does not compare "functional
equivalence".

What makes it hold:

* `SOURCE_DATE_EPOCH` from the commit clamps every mtime, the `ar` member
  timestamps, the RPM build time and the RPM payload mtimes.
* `dotnet publish` runs with `Deterministic` and `ContinuousIntegrationBuild`.
* `tesserafin.staticwebassets.endpoints.json` has its `Last-Modified` values
  normalised to the commit time — the one genuinely non-deterministic file in the
  publish output, normalised the same way the container build normalises it.
* `dpkg-deb --root-owner-group` removes the build user's identity; `xz` and
  `gzip -n` carry no timestamp.
* `rpmbuild` runs in a digest-pinned image with `_buildhost`,
  `use_source_date_epoch_as_buildtime` and `clamp_mtime_to_source_date_epoch`
  set, and with debuginfo, stripping and build-id links disabled.
* `tar --sort=name --owner=0 --group=0 --numeric-owner`.

---

## 11. What this work does not do

No signing keys, no signed APT or DNF repository, no repository metadata, no
public release, no publication of any artifact, no automatic updates. The
workflow has no `release:` trigger and no push step; artifacts exist only as
workflow artifacts.

The packaging checks are **not** branch-protection required checks. Branch
protection lists a fixed set of contexts, and this work does not modify it.

### Deferred to L1

* signed APT and DNF repositories, and the metadata they need
* signing-key management and rotation
* stable-channel publication
* a proven clean upgrade from a downloaded L0 package to a repository-installed
  one
