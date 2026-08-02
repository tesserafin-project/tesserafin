# Secret-configuration model — server and container

What this document is: a description of the secret channels this system **actually has**,
written so that an operator can tell where a credential is supposed to live and a reviewer
can tell when one has ended up somewhere it should not.

What it is not: a wish list. Every mechanism named below exists in the tree today. Where a
mechanism that a reader might expect does **not** exist — Docker secrets, `_FILE` indirection,
a vault integration, a project cloud — that is stated as an absence rather than quietly
omitted, because an operator who assumes an absent mechanism exists will store a credential
in the wrong place.

No credential value appears in this document, and none may be added to it.

Related: [`docs/metadata-provider-keys.md`](metadata-provider-keys.md) (the operator guide for
provider keys), [`docs/provider-auth-audit.md`](provider-auth-audit.md) (the structural gate
that keeps provider credentials out of the compiled server),
[`docs/container/A2-persistent-state.md`](container/A2-persistent-state.md) (what `/config`
holds), [`docs/local-ci.md`](local-ci.md) (how to run the secret scan locally).

---

## 1. The administrator account

**Where it comes from:** a browser. The first-run wizard asks for it after the container is
already running.

**Where it is stored:** inside `/config`, in the server's own database, hashed by the server.

**Where it is NOT:** it is not a Compose variable, not an environment variable, not a build
argument, and not an image default. There is no built-in account and no default password.
A freshly pulled image has no credential of any kind baked into it; until an operator
completes the wizard, no account exists.

This is the single most common place a project leaks an administrator credential — a
`ADMIN_PASSWORD=` line in a committed Compose file — and this project has no such line to
leak. Do not add one.

## 2. Metadata provider keys

TheMovieDb, TheAudioDB and OMDb keys are **operator-supplied**, entered on each plugin's
settings page in the web dashboard.

**Where they are stored:** `/config/plugins/configurations/`, one XML file per plugin, inside
the `/config` volume.

**Where they are NOT:** the server ships none of its own. This was not always true — three
inherited upstream credentials were compiled into `Tesserafin.Providers.dll` until
[#173](https://github.com/tesserafin-project/tesserafin/pull/173) and
[#176](https://github.com/tesserafin-project/tesserafin/pull/176) removed them. Their absence
is now enforced on every `./ci/run.sh` by the provider-authentication structural audit, which
reads the **compiled** assembly rather than the source, because the compiler folds
concatenation, interpolation and fragment-splitting into a single literal that a source scan
would never see.

With no key configured, those providers are inert: no request is issued, lookups return the
architecture's normal empty result, and one warning per plugin names the setting — never its
value. Nothing else about the server is affected.

## 3. `/config` is the secret boundary

`/config` holds the administrator's hashed credential, every plugin configuration including
the provider keys above, any device/API keys the operator has issued, and — if the operator
configured HTTPS — their TLS private key.

Consequences an operator must act on:

* **A `/config` backup is a credential backup.** Store it the way you would store the keys
  themselves: restricted permissions, and encrypted if it leaves the host.
* **Restrictive filesystem permissions.** `/config` and anything under
  `/config/plugins/configurations` should be readable only by the account running the
  container. An operator-supplied TLS private key in particular must be mode `0600` and owned
  by that account.
* **Moving `/config` between hosts moves the credentials with it.** That is by design — it is
  what makes the upgrade path work — but it means a `/config` volume copied to a shared
  machine has published its contents.

`/data` and `/cache` are not credential stores. Treat them as media and derived data.

## 4. Compose, `.env` and `.env.example`

**`docker-compose.yml`** commits environment variables that are *operational configuration*,
not credentials: the log level, the log rendering format, the image reference, the published
port and the volume names. Every one of them is safe to read, safe to commit and safe to put
in a bug report.

**`.env`** is local-only. `docker compose` reads it automatically, it is not tracked, and
`.gitignore` now covers `.env` and its `.env.*` variants explicitly. That rule stands **even
though `.env` currently holds nothing secret** — the value of the rule is that it is already
in place on the day someone puts something secret there, not that it is needed today.

**`.env.example`** is tracked on purpose and contains exactly two kinds of thing: commented
placeholders, and immutable image identity (a digest pin and the matching version tag). It
carries no credential and must never be used to carry one. `.gitignore`'s `.env.*` rule
explicitly negates it, so the example stays tracked.

## 5. GHCR credentials

Pulling the image needs a registry credential, because the packages are private.

**Where it belongs:** the host's Docker credential store, established once with
`docker login ghcr.io`.

**Where it must never go:** `.env`, `docker-compose.yml`, a build argument, an image layer, an
environment variable inside the container, or any file in this repository. The server never
needs a registry credential at run time — only the host's Docker daemon does, and only while
pulling.

A GHCR token pasted into `.env` would be committed by the next person who does not know it is
there. This is exactly the class of mistake the scan in [`docs/local-ci.md`](local-ci.md)
exists to catch, and it would catch it — *after* the commit, not before. See §8.

## 6. The database

SQLite, in `/config`, opened by a local file path. There is no connection string, no database
user, no password and therefore **no external database credential contract** at all today.

If that ever changes, it changes this document first.

## 7. Mechanisms that do NOT exist

Named explicitly so that nobody stores a credential in one and assumes it was read:

* **No `_FILE` indirection.** `TESSERAFIN_SOMETHING_FILE` is not read by anything. A path
  placed there is ignored.
* **No Docker secrets.** The Compose file declares no `secrets:` block, and the container
  reads nothing from `/run/secrets`.
* **No vault, no KMS, no external secret manager** integration of any kind.
* **No Tesserafin cloud.** There is no account, no telemetry endpoint and no remote service
  this server must authenticate to. Nothing here introduces one, and no future change may
  introduce a *mandatory* one without replacing this section.

The complete supported set is: the browser wizard, the plugin settings pages, and the
`/config` volume they both write to.

## 8. The image itself

Build arguments, image labels, build history and the bundled frontend assets must never carry
a credential. Verified on the installation-default digest rather than assumed:

* `Env` holds paths, the log format and .NET runtime flags only;
* the OCI and `org.tesserafin.*` labels hold versions, revisions and asset digests;
* every `ARG` in the build history is a version, a ref, a UID/GID or an asset digest;
* a Gitleaks filesystem scan of the exported rootfs reports zero findings.

**That last bullet is the weakest of the four and must not be over-read.** A Gitleaks
filesystem scan does not decode .NET metadata heaps. A C# `const string` is inlined into the
`#US` user-string heap as UTF-16LE, where neither Gitleaks nor a plain ASCII `strings` pass
can see it — which is precisely how three inherited provider credentials shipped in a
published image that scanned clean. The proof that the image is free of them is the
**binary-aware provider-authentication audit** over the managed assemblies, not the filesystem
scan. Whenever this project says "the image scan is clean", both halves must be said.

## 9. What the repository-owned gate does and does not do

`ci/secret-scan.sh` scans the current tree and the complete history, fails closed on three
verdicts (`0` clean, `1` findings, `2` indeterminate), and runs automatically on every pull
request, every push to `master`, and weekly.

It **detects**. It does not **prevent**. By the time it runs, GitHub has already accepted the
push and the object already exists in the repository. Preventing the push requires GitHub-
native push protection, which requires GitHub Secret Protection, which a private repository on
a free organisation plan does not have. That gap is tracked by
[#96](https://github.com/tesserafin-project/tesserafin/issues/96) and
[#94](https://github.com/tesserafin-project/tesserafin/issues/94), and nothing in this
repository closes it.

It is also blind to two classes this project has actually encountered — short provider
credentials, and values compiled into .NET metadata heaps. Those are covered by the structural
audit in [`docs/provider-auth-audit.md`](provider-auth-audit.md), which identifies a credential
by *where it is used* rather than by what it looks like.

## 10. If a credential is committed anyway

1. **Rotate it first.** A commit is public to everyone who can read the repository from the
   moment it is pushed; deleting it later does not un-publish it.
2. **Do not rewrite history to hide it.** This repository's policy is no history rewrite. The
   historical baseline in `ci/secret-history-baseline.json` records inherited findings with
   their provenance and disposition instead — exact fingerprints only, never a path-wide,
   rule-wide or regex-wide suppression.
3. **Do not add the fingerprint to `.gitleaksignore` to make CI green.** A new finding needs an
   owner disposition, not a suppression. The gate is designed so that a baselined historical
   fingerprint cannot excuse a current-tree recurrence of the same value — there is a control
   that proves it.
