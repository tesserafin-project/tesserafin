# Tesserafin versioning policy

Issues [#92](https://github.com/tesserafin/tesserafin/issues/92) ([A6]),
[#101](https://github.com/tesserafin/tesserafin/issues/101) ([E1]),
[#127](https://github.com/tesserafin/tesserafin/issues/127).

This document is authoritative for **which numbers Tesserafin publishes, where it
publishes them, and how a consumer is allowed to resolve a release**. It sits
above the mechanism: `docker/version-contract.sh` implements the tag derivation,
[A6](./container/A6-versioning-and-upgrades.md) records how that mechanism was
proven, and this document states the policy both of them serve.

---

## 1. The public version epoch

**Tesserafin public SemVer begins at `1.0.0`.**

Tesserafin is a fork. It inherited a `12.x` server version line and a `13.x` web
version line from its upstream history. Those numbers describe an upstream
lineage, not a Tesserafin release history: Tesserafin has never published a
release, so it has no releases numbered 1 through 11. Shipping a first public
release as `12.0.0` would claim eleven major versions of public compatibility
history that does not exist.

The first public Tesserafin release is therefore numbered `1.0.0`, and the
server and the web client share that number.

### 1.1 What the inherited artifacts are

Every `ghcr.io/tesserafin/tesserafin:12.0.0-dev.*` server image and every
`ghcr.io/tesserafin/tesserafin-web-assets:13.0.0-dev.*` web-assets image
that already exists:

* **is retained.** None is deleted, retagged, moved or rewritten. They are the
  reproducibility record and the evidence behind the A1–A7 gates.
* **is an internal, unsupported development artifact.** It predates the public
  version epoch.
* **is not a public release.** It was never announced, never carried a channel
  tag, and never appeared in a GitHub Release.
* **is not a valid input to an automatic supported-upgrade decision.** No
  updater, client or operator tool may treat one of these images as a version a
  supported installation can be on, or upgrade from, or downgrade to.

The transition from an inherited `12.x` development image to the `1.x` line is
**neither a supported upgrade nor a supported downgrade**. It is a change of
version epoch. An operator who ran a `12.0.0-dev.*` image during development and
wants to move to the `1.x` line is performing a manual migration outside the
supported graph, and the project makes no compatibility promise about it.

### 1.2 What the inherited artifacts still prove

The `12.x` evidence remains valid **as evidence**. The A6 upgrade-round-trip runs
recorded in [A6](./container/A6-versioning-and-upgrades.md) prove that the
upgrade harness preserves users, libraries, configuration and playback state
across two immutable builds. That property is about the harness and the
persistence layer, and it does not stop being true because the version numbers
changed. What those runs do **not** establish is a forward-migration boundary
inside the `1.x` epoch — see §5.

### 1.3 Consequence for plugins

`ApplicationVersion` is read from the assembly version
(`Tesserafin.Server.Core/ApplicationHost.cs`), and the plugin manager admits a
plugin only when `ApplicationVersion >= targetAbi`
(`Tesserafin.Server.Core/Plugins/PluginManager.cs`,
`Tesserafin.Server.Core/Updates/InstallationManager.cs`). At `1.0.0`, any plugin
declaring a `targetAbi` in the upstream `10.x` or the inherited `12.x` range is
reported as not supported.

This is intended. A Tesserafin plugin targets a Tesserafin ABI, and the
Tesserafin ABI starts at `1.0.0`. The policy does not create a compatibility
claim over upstream plugin binaries, and it does not silently accept them either.

---

## 2. Package namespaces

| Package | Role | Visibility |
|---|---|---|
| `ghcr.io/tesserafin/tesserafin` | **pre-v1 server archive** — every inherited `12.0.0-dev.*` image | private |
| `ghcr.io/tesserafin/tesserafin-server` | **canonical v1+ server package** | private until [E3] ratifies publication |
| `ghcr.io/tesserafin/tesserafin-web-assets` | web-assets package, spanning both epochs | private until [E3] ratifies publication |

Rules:

* Only `tesserafin-server` may ever receive future stable or channel tags.
* The archive `tesserafin` **must never** receive `latest`, `stable`, `1`, `1.0`
  or any other public-release alias. It is immutable: nothing is added to it, and
  nothing is removed from it.
* `tesserafin-web-assets` keeps its inherited `13.0.0-dev.*` versions and gains
  `1.0.0-dev.*` versions. The web-assets package is a build input, not a product
  a user installs, so it does not carry product channel aliases either.

The archive reference is a strict **prefix** of the canonical reference. Any
check that identifies a package must compare the full repository reference or
stop at the `:`/`@` boundary; a substring test for `tesserafin` matches all three
packages and proves nothing.

---

## 3. Version selection

A client, updater or installer **must never**:

* enumerate arbitrary GHCR tags;
* sort those tags globally by SemVer;
* assume the numerically largest tag is the supported release.

Under this policy the numerically largest tag in the organisation is
`12.0.0-dev.*` — an unsupported development artifact from the previous epoch —
and it will stay the largest for as long as the `1.x` line runs. "Greatest
SemVer tag wins" therefore does not merely risk the wrong answer here; it is
guaranteed to produce it.

Supported resolution uses an **explicit source of authority**, and only one of:

1. an **immutable exact version or digest** the operator names
   (`tesserafin-server:1.0.0`, or `tesserafin-server@sha256:…`);
2. a **documented channel tag** from the table in §4;
3. a **curated release manifest** published by the project (not yet defined).

Historical tags are not channels. The presence of a tag in a registry is not a
statement that the project supports it.

Tesserafin ships no auto-updater today, and this policy is what any future one
must be built against.

---

## 4. Planned channel contract

The tag classes below are the complete set `docker/version-contract.sh` is
allowed to derive. **This section documents the contract; it does not authorise
publication.** Everything except the immutable development class is unpublished
at the time of writing.

| Class | Tag shape | Mutability | Published today |
|---|---|---|---|
| immutable development | `1.0.0-dev.<12-char source commit>` | immutable | **yes**, private |
| immutable source | `sha-<40-char source commit>` | immutable | **yes**, private |
| release candidate | `1.0.0-rc.N` | immutable | no |
| pre-release channel | `preview` | mutable | no |
| stable exact | `1.0.0` | immutable | no |
| minor alias | `1.0` | mutable | no |
| major alias | `1` | mutable | no |
| stable channel | `stable` | mutable | no |
| default channel | `latest` | mutable | no — only if [E3] explicitly ratifies it for stable releases |

`docker/version-contract.sh` enforces that `latest` is reachable only from the
`stable` channel, and `docker/version-contract.test.sh` asserts it.

No mutable tag has been published in either package. The first public release is
gated on [#103](https://github.com/tesserafin/tesserafin/issues/103) [E3].

---

## 5. The forward-migration gate (#127)

Two promises are deliberately distinct.

**Before v1 — prove one real forward migration inside the `1.x` epoch.**
[#127](https://github.com/tesserafin/tesserafin/issues/127) is satisfied
only by a run of

```
docker/upgrade-roundtrip.sh \
  --baseline  <the v1 baseline digest> \
  --candidate <a digest built after a real schema migration landed> \
  --require-migration
```

that applies **more than zero** pending migrations and preserves every existing
assertion. A dummy migration, a version-only migration or a configuration no-op
does not satisfy this gate and must not be added to close it.

**After v1 — maintain documented compatibility across one supported minor
upgrade.** Once `1.0.0` is public, the project commits to a documented upgrade
path across at least one supported minor step within the `1.x` line.

The `12.x` A6 evidence satisfies neither promise, because both are scoped to the
`1.x` epoch.

---

## 6. Where each number lives

| Surface | Authority |
|---|---|
| `SharedVersion.cs` `[assembly: AssemblyVersion]` | **the** canonical server product version |
| `*/…​.csproj` `<VersionPrefix>` | derived; must equal the canonical version |
| `ApplicationVersion`, `/System/Info`, `/health` | read from the assembly at runtime |
| `openapi/openapi.json` `info.version`, `x-tesserafin-version` | generated from the assembly version |
| `openapi/contract.lock.json` | pins the generated contract by version and sha256 |
| server image tags, `org.opencontainers.image.version` | derived by `docker/version-contract.sh` from the canonical version |
| `org.opencontainers.image.revision` | the exact source commit; never a version |
| `package.json` `version` (tesserafin-web) | **the** canonical web product version |
| web-assets image tags, `org.opencontainers.image.version` | derived from `package.json` by `docker/build-assets.sh` |
| `org.tesserafin.web.revision` / `.version` / `.assets.image` | the exact web commit, web product version and immutable assets digest bundled into a server image |

Nothing else derives a version. A new consumer asks one of these; it does not
re-implement the derivation.
