# W1-R — durable retention for the Windows FFmpeg build inputs

Status: **implemented, not published.** Issue [#236]. Frozen base
`6df7cc434048f53a1952d29d9df185ced06bcb05`.

This document records why the native Windows FFmpeg runtime could not be built
reproducibly without a new mechanism, what that mechanism is, and exactly where
its security boundary sits.

**This is build infrastructure. Nothing here is shipped to a user.** The artifact
it describes carries compilers and build tools. It carries no FFmpeg binary, no
Tesserafin server, no installer and no signed anything.

---

## 1. Why an upstream MSYS2 URL is not a pin

W1's contract requires the toolchain to be an immutable input: two builds a year
apart must resolve the same bytes. Linux already satisfies that —
`ci/ffmpeg/builder/Dockerfile` pins a Debian base by digest and installs every
package from a **fixed `snapshot.debian.org` timestamp**, a permanent immutable
archive of exact package bytes.

**MSYS2 publishes no equivalent.** Measured, not assumed:

| probe | result |
| --- | --- |
| `repo.msys2.org/snapshot/` | 404 |
| `mirror.msys2.org/snapshot/` | 404 |
| `msys2/msys2-autobuild` releases | rolling `staging-*` tags whose assets are deleted once published |
| seven regional mirrors | all track the origin's current state; none is an archive |

Superseded versions *do* survive for a long window — the live `clang64` index
carries 43 distinct `llvm` archives back to `19.1.4-1` — but that window is
bounded, unpublished and demonstrably enforced: there is **no `llvm-17.*` and no
`llvm-18.*` in the repository at all**. Retention exists; a retention *contract*
does not.

So a URL plus a digest gives integrity — you cannot be served different bytes
without noticing — but not availability. For a component whose corresponding-source
and provenance obligations outlive a release, that is not enough.

The escape hatch of deriving the toolchain from source is closed by the
repository's own doctrine, stated in `ci/ffmpeg/builder/Dockerfile`:

> The toolchain is an INPUT. This image never builds a compiler — that is what
> makes the FFmpeg build reproducible rather than merely repeatable.

## 2. The owner's ruling

W1-A stopped on this and reported it rather than weakening the requirement. The
owner authorised **one** repository-controlled GHCR package:

```
ghcr.io/tesserafin-project/windows-ffmpeg-build-inputs
```

limited to immutable FFmpeg-for-Windows build inputs: MSYS2 package archives and
signatures, the lock and inventory manifests, and the metadata needed to verify
and consume them. A manual publisher may run **only from trusted `master`**, with
job-level `packages: write` and no other secret than the scoped `GITHUB_TOKEN`.
Consumption is by exact `sha256:` manifest digest only.

The ruling authorises no FFmpeg binary, no server runtime, no tag or release, no
MSI, no signing, no deletion automation and no broad permission. It is recorded
on [#236].

## 3. Ingress versus consumption

These are different privileges and are implemented as different code paths.

**Ingress** — `ci/windows/build-inputs/ingest.py` — is the *only* place allowed
to contact the live MSYS2 repository, and it may do exactly one thing there:
fetch the exact filenames a reviewed, committed lock already names. It resolves
nothing, expands no group and follows no dependency. It re-fetches the two
repository databases and stops if their SHA-256 has moved since the lock was
reviewed; it rejects a response whose final URL names a different file; and it
verifies every archive's SHA-256 *before* admitting it.

**Consumption** — `consume.ps1` (registry) and `install-locked.ps1` (local
bundle) — never contacts MSYS2 at all. It verifies every path against the
bundle's own `manifest.sha256`, **empties every MSYS2 mirror**, and installs with
`pacman -U` over local files. Emptying the mirrors is the proof rather than
housekeeping: if the locked set were incomplete, pacman would need a repository
and would fail.

`pacman -S`, `pacman -Syu`, live dependency resolution, group expansion against a
moving database and installation from an upstream URL are PROHIBITED, and a
negative control scans the tracked scripts and workflow for any invocation.

## 4. The closure, and why it is bigger than W0's

W0's spike installed 8 packages' worth of closure — 141 packages, 329.3 MiB. That
is not enough to *build* the runtime, because upstream's `msys2/build.sh` drives
36 dependency recipes through `makepkg-mingw -sLfi`, and `-s` resolves each
recipe's `makedepends` against the live repository. W1 cannot do that, so those
makedepends are hoisted into the closure and installed from the locked set
beforehand.

The roots are derived from authoritative metadata, never restated here:

| root source | how it is read |
| --- | --- |
| FFmpeg upstream commit | `ci/ffmpeg/components.json`, asserted equal to `F0_UPSTREAM_COMMIT` in `ci/package/pins.env` |
| interactive install set | upstream `.github/workflows/_meta_win_clang_portable.yaml` at that commit, CLANG64 row |
| dependency build tools | the `depends`/`makedepends`/`checkdepends` of upstream's 36 `msys2/PKGBUILD/*/PKGBUILD` recipes |
| `base-devel` | ships `makepkg-mingw` by way of `pacman` |

The recipe arrays are read by **sourcing each PKGBUILD in bash**, not by regular
expression, because upstream varies them in shell: `20-mingw-w64-fftw` appends
`-gcc-fortran` only when the prefix is not a clang one. A regex would have added
a package that does not exist in CLANG64. The generator's first run failed
exactly that way, and failing closed is what surfaced it.

Result: **35 roots → 246 packages, 388.8 MiB compressed, 2.51 GiB installed.**

## 5. The lock

`ci/windows/build-inputs/msys2-lock.json`, schema version 1. Every package
records repository, name, version, architecture, exact filename, SHA-256,
compressed and installed size, declared dependencies, licence metadata, the
upstream retrieval URL and its detached-signature URL. MSYS2 publishes a `.sig`
for every one of the 246.

`verify-lock.py` is offline and **fails closed on unknown**: an unrecognised
schema version, an unknown field, a missing required field, a duplicate name or
filename, a filename that disagrees with its own name/version/architecture, a URL
outside the MSYS2 repository, an unsafe filename, or a dependency not satisfied
from inside the lock. Failing closed on an unknown field is deliberate and is the
opposite of usual JSON tolerance: a field the validator does not understand is a
semantic it does not enforce, and a lock carrying unenforced semantics would look
validated while being unvalidated.

## 6. Deterministic construction

Identity is the OCI **manifest digest**, so every field an implementation is free
to vary is fixed:

* the layer is an **uncompressed** tar — a compressor is another implementation
  whose output can change between versions;
* entries are in sorted path order, uid/gid 0, empty owner/group, mode 0644,
  mtime `SOURCE_DATE_EPOCH = 0`, USTAR format, no PAX/GNU extension headers;
* symlinks and non-file entries are refused;
* JSON is sorted-key, two-space indent, LF, trailing newline;
* annotations carry no timestamp, workflow id, runner path or branch name.
  **`org.opencontainers.image.created` is deliberately absent** — it is the single
  most common reason an "identical" artifact ends up with two digests;
* the environment is pinned to `LC_ALL=C`, `TZ=UTC`.

Media types are documented and versioned:

| role | media type |
| --- | --- |
| artifact | `application/vnd.tesserafin.windows-build-inputs.v1+json` |
| config | `application/vnd.tesserafin.windows-build-inputs.config.v1+json` |
| layer | `application/vnd.tesserafin.windows-build-inputs.layer.v1.tar` |
| manifest | `application/vnd.oci.image.manifest.v1+json` |

Publication uses `oras manifest push` with the exact precomputed bytes and
**never `oras push`**, which would construct a manifest of its own. ORAS itself is
pinned by version *and* SHA-256 in `tools.lock.json` and verified before use; a
runner-preinstalled registry client is never accepted, because its identity is
whatever the image happens to carry — the same rolling-input problem this
mechanism exists to remove.

## 7. Digest-only consumption

```
ghcr.io/tesserafin-project/windows-ffmpeg-build-inputs@sha256:<digest>
```

A tag may exist for discovery and is **never** an accepted identity. `contract.py`
refuses a reference that is not digest-pinned, one that carries both a tag and a
digest, one naming any other package, and a malformed digest. A tag moving or
disappearing cannot affect a pinned consumer.

Expiring Actions artifacts are never production inputs. The PR validation
uploads short-lived evidence only, and that evidence is not the retention
mechanism — it is the record that the mechanism worked.

## 8. The security boundary

| | validation path | publication path |
| --- | --- | --- |
| trigger | `pull_request`, `workflow_dispatch` | `workflow_dispatch` **only** |
| ref | any | `refs/heads/master` **only** |
| permissions | `contents: read` | `contents: read` + job-level `packages: write` |
| secrets | none | scoped `GITHUB_TOKEN` only |
| can publish | **no** | yes, and only the reviewed digest |

`pull_request_target` is never used. All actions are SHA-pinned and every
checkout is `persist-credentials: false`. Every job asserts its checked-out HEAD
equals the intended commit before doing anything — W0 was bitten by a synthetic
merge ref producing confident-looking evidence attributable to no reviewable
commit, and the same guard is used here.

The trusted-branch check is asserted **inside the publisher's own steps**, not
only as a job-level `if:`. A job-level `if:` that evaluates false reports
SUCCESS, and a security boundary that reports success when it did not run is not
a boundary.

The publisher is *told* what it may push: `expected-digest.json` carries the
reviewed manifest digest, the run rebuilds the bundle on the trusted runner, and
`assert_expected_digest` refuses if what it built is not what was reviewed.
Nothing is pushed on a mismatch. After pushing, the manifest is read back by
digest and compared byte for byte with what was pushed.

`environment: windows-build-input-publication` is declared on the publisher and
was **created explicitly** by W1-R-B, before any dispatch — GitHub would
otherwise auto-create it unprotected on first use, which would be simulating
approval rather than obtaining it. Its policy, read back through the API:

| setting | value |
| --- | --- |
| required reviewers | `all3f0r1` (the only account with access) |
| `prevent_self_review` | `false` — see the limitation in §11 |
| deployment branches | protected branches only; no custom policy |
| protected branches in this repository | `master`, and only `master` |
| environment secrets / variables | none |
| wait timer | none |

The deployment therefore pauses for a human before the publisher runs, and the
only branch it can ever run from is `master`.

## 9. What the hosted runs found

Every one of these was found by a check refusing, rather than by a build going
quietly wrong.

**Git for Windows' bash is not MSYS2's.** The installer originally derived the
MSYS2 root from `bash.exe` on PATH. On a hosted runner that resolves to Git for
Windows, whose tree has no `etc/pacman.d` at all — so there was no mirrorlist to
empty, and the script stopped with *"the no-upstream proof would be vacuous"*
instead of installing and reporting success. The root now comes from
`setup-msys2`'s own output and `pacman.exe` is asserted present.

**The core runtime cannot be replaced underneath the pacman that is using it.**
The runner's MSYS2 carries an older `msys2-runtime` than the lock. Installing all
246 packages in one transaction swaps `msys-2.0.dll` while pacman is running on
it, and every subsequent post-install script dies with `could not fork a new
process (Resource temporarily unavailable)`. MSYS2's own core update has exactly
this shape and exactly this remedy: update the runtime, leave the shell, return
in a new one. The installer therefore runs two phases, the second in a new
process. **This changes when packages are installed, never where they come from**
— it is still `pacman -U` over local files with no mirror configured.

**A virtual `%PROVIDES%` was beating a real package to its own name.** This one
was a genuine defect in the closure, and it is the reason the Windows leg was
worth running at all. `base` depends on `msys2-runtime`. The compatibility
package `msys2-runtime-3.3` declares `provides: msys2-runtime=3.3.6`. The
resolver built one name map in a single pass, so whichever package it visited
first took the name — and the *compat* package won. The lock therefore carried
`msys2-runtime-3.3` and not `msys2-runtime`, and installing it **downgraded the
runtime out from under the running MSYS2**:

```
:: msys2-runtime-3.3-3.3.6-16 and msys2-runtime-3.6.10-2 are in conflict
error: could not fork a new process (Resource temporarily unavailable)
```

The rule is now explicit and in two passes: **a real package always wins the name
it actually has, before any other package's `%PROVIDES%` may claim it.** A
negative control encodes exactly this case, so a future resolver change that
reintroduces it fails in seconds instead of on a Windows runner.

Worth stating plainly: the two Linux bundle passes were byte-identical and green
across all three runs while the lock was wrong. Reproducibility says two builds
agree, not that what they agree on is correct. **Only the native install found
this.**

**The gpg on a Windows runner reads a Windows path as a relative POSIX one.**
Signature verification died before it verified anything:

```
gpg: keyblock resource '/d/a/tesserafin/tesserafin/C:\Users\RUNNER~1\...\pubring.kbx':
No such file or directory
```

The gpg present before MSYS2's own `gnupg` package is installed — and installing
it is exactly what this gate guards — is Git for Windows', an MSYS binary. The
interpreter handing it paths is native Windows Python, so `--homedir`, the key
file, the signature and the archive all cross a namespace boundary. Translating
only the first would have turned the next failure into a missing `VALIDSIG`,
which reads like a signature finding rather than a path bug. The translator is
`cygpath` from the same `usr/bin` as the selected gpg, and both are reported in
the summary, because a null translator beside an MSYS gpg is the same bug wearing
a different disguise.

**`--needed` made the runtime phase a no-op.** See §9c: the phase that exists to
replace the runtime printed `skipping` and did nothing, on every runner whose
image already carried the locked version.

## 9a. Attribution: the signing root of trust

The lock's SHA-256 answers *are these the bytes we reviewed?*. It cannot answer
*did MSYS2 produce them?* — a lock generated against a compromised mirror would
be internally consistent and wrong. Those are two different decisions and this
repository makes both, separately:

* **the committed lock is the integrity decision.** An archive is admitted only
  if its SHA-256 equals the reviewed value;
* **the signature is the attribution decision.** It proves those exact bytes were
  signed by an MSYS2 packager we accept.

Neither substitutes for the other. A valid signature never admits an archive
whose digest disagrees with the lock, and a matching digest never admits an
archive whose signature does not verify. Verification happens **before** an
archive enters the bundle.

`trust/msys2-signing-keys.asc` is the accepted signing material, pinned as exact
bytes; `trust/trust-root.json` records its provenance, its SHA-256, its length
and the accepted fingerprints. `signing.py` runs GnuPG in a throwaway home with
`--no-default-keyring`, no keyserver, no auto key location and no dirmngr, and
strips every inherited `GNUPG*`/`GPG*` variable from the child environment — the
runner's ambient keyring cannot make a signature verify. After import, the
fingerprints GnuPG actually holds must **equal** the allowlist, not merely
include it, so editing either the key bytes or the list narrows nothing open.

Acceptance is positive: a `VALIDSIG` whose fingerprint is in the allowlist.
`BADSIG`, `ERRSIG`, `EXPSIG`, `EXPKEYSIG`, `REVKEYSIG`, `NO_PUBKEY`, a missing
signature file, an unparsable signature, and a zero exit with no `VALIDSIG` at
all are each refusals.

## 9b. The build prefix holds exactly the locked set

"Everything the lock names is installed" is also satisfied by a prefix carrying
an undeclared compiler. An undeclared package can influence the FFmpeg build
while appearing in no lock, no bundle and no published provenance — and *looks
harmless* is not a property this repository can check, whereas *is in the
reviewed lock* is.

After installation the prefix must hold **exactly** the locked set: same names,
same versions, same architectures. A future runner image that quietly gains a
package fails here. Pre-existing packages are acceptable only as members of the
lock at the locked version, which after `pacman -U` over every locked archive is
a statement about what the transaction achieved rather than a courtesy extended
to the base image.

The ruling lives in `installed-set.py` rather than in the PowerShell that
collects the observation, so it can be exercised on Linux against the failures a
hosted Windows job cannot stage on demand.

## 9c. The runtime replacement is forced, every run

The phase-one transaction deliberately carries **no** `--needed`. With it, a
runner whose MSYS2 already holds the locked runtime printed

```
warning: msys2-runtime-3.6.10-2 is up to date -- skipping
```

and the phase that exists to replace `msys-2.0.dll` never ran. It would have been
exercised only by the accident of an outdated runner image and would have rotted
silently the day the image caught up.

The proof recorded each run: pacman's own transaction line (`skipping` is refused
by name), the phase-one and phase-two shell pids — which differ, because the old
runtime went with the process that mapped it — `uname -r` and `pacman -Q` taken
in that new process before it installs anything, and afterwards a check that the
`msys-2.0.dll` on disk is byte for byte the packaged one and that the file list
pacman records equals the archive's members. `pacman -Qkk` is deliberately not
the check: it would only prove pacman agrees with itself.

## 9d. The registry protocol is rehearsed before it is performed

The publication path was, until W1-R-B, the only part of W1-R never executed: it
would have run for the first time against GHCR, with `packages: write`, on
trusted `master`.

`oci-protocol.sh` is that path, as one implementation, and the `registry` job
runs it against an ephemeral OCI Distribution registry — pinned by immutable
digest — on the runner's loopback, with no `packages: write` and no external
publication. Push the layer blob, push the config blob, push the exact reviewed
manifest bytes addressed by digest, read back by digest, compare bytes, compare
config and layer digests **and sizes**, re-hash the blobs that came back, then run
the ordinary consumer verification over the pulled content. Submitting the same
digest twice is idempotent.

Thirteen controls cover the ways the round trip can go wrong: a manifest whose
blobs the registry does not hold, bytes submitted under a digest that is not
their own, a tag (nothing is ever tagged, so a tag resolves to nothing), a
different repository name, a rewritten manifest, an altered or truncated layer,
an altered or absent config, and a reviewed descriptor claiming a size the bytes
do not have.

## 10. Update and revocation

An existing bundle is **never mutated**. If W1 later needs another package, or a
toolchain moves:

1. regenerate the lock with `resolve-closure.py` — it re-reads the pins and the
   upstream recipes, so the roots cannot drift silently;
2. review the diff. A new package, a version change and a database digest change
   are all visible in `msys2-lock.json`;
3. the deterministic build produces a **new** manifest digest, because the lock
   digest is inside the config and the archives are inside the layer;
4. record the new digest in `expected-digest.json` and publish it;
5. consumers move deliberately, by editing the digest they pin.

Old digests are retained rather than deleted: a released runtime's corresponding
source and provenance point at the toolchain it was actually built with, and
deleting that digest would break the claim retrospectively. **No deletion or
cleanup automation is authorised.** Revocation means publishing a successor and
recording why the predecessor must not be used, not removing bytes someone's
provenance already names.

## 11. Known limitations

* **The publication environment has one eligible reviewer, and it is the owner.**
  `windows-build-input-publication` requires a human approval before the
  publisher runs, and `all3f0r1` is the only account with access to give it. GitHub
  permits that approval — `prevent_self_review` is `false`, because with a single
  eligible reviewer setting it `true` would make the deployment unapprovable
  rather than more separated. This is a deliberate pause for a human decision,
  and it is **not** separation of duties. It becomes separation of duties the day
  a second maintainer exists, by adding them as a reviewer and flipping
  `prevent_self_review`.
* **GHCR is retention under organisation control, not a third-party archive.**
  It is non-expiring and repository-linked, which upstream MSYS2 is not, but it
  depends on the organisation continuing to exist and on nobody deleting the
  package. The no-deletion-automation rule exists for that reason.
* **The bundle is `win-x64` / CLANG64 only.** `win-arm64` is not promised for 1.1
  and its closure is not resolved.
* **The lock is a snapshot of a moving repository.** Regenerating it later will
  produce different versions, which is why regeneration is a reviewed change
  producing a new digest, never an in-place refresh.
* **The pull request publishes nothing.** Until the publisher is dispatched from
  `master`, the human reviewer approves the deployment and the stored manifest is
  read back, the retention claim is *designed and proven reproducible*, not
  *in effect*.
* **The local registry is not GHCR.** The rehearsal proves the protocol, the
  ordering, the byte comparison and the refusals against a conformant OCI
  Distribution implementation. It cannot prove GHCR's own behaviour — token
  scoping, package linkage and visibility are exercised only by the real
  publication and the post-publication consumer run.

## 12. Sequence

| loop | does |
| --- | --- |
| **W1-R-A** | implemented and validated in a draft PR. The GHCR package did not exist at the end of it. |
| **W1-R-B** (this) | repairs provider ambiguity, signature authentication, exact installed-set equality and the forced runtime replacement; rehearses the registry protocol locally; creates the protected environment; merges; dispatches the publisher from `master`; verifies the stored digest and records it. |
| **W1-A2** | resumes the FFmpeg runtime build, consuming that digest and only that digest. |

[#236]: https://github.com/tesserafin-project/tesserafin/issues/236
