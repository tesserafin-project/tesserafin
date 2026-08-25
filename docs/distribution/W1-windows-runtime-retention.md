# W1-A4 — durable retention of the accepted win-x64 FFmpeg runtime

Issue [#236]. Sibling of [W1-windows-build-input-retention.md](W1-windows-build-input-retention.md),
which retains the build **inputs**. This one retains the **runtime**, and the
difference is not cosmetic: the build inputs are infrastructure, while the
runtime is a conveyed GPL-3.0-or-later binary. Every additional obligation in
this document follows from that one fact.

## 1. Why this exists

W1-A3 built a native win-x64 FFmpeg runtime, proved two runner allocations
produced identical bytes, and was independently reviewed and merged as
`83e23b9579404883c2d3e93f6f3ac8748061c618`.

Those bytes exist in exactly one place: the Actions artifacts of run
[32750491696], which **expire on 2026-09-23**.

The W1 contract already states that expiring Actions artifacts cannot be
production inputs. So W2 cannot consume what exists today, and the runtime is
not "retained" in any sense that survives a month. This document describes the
machinery that turns it into a durable, immutable, digest-addressed production
input.

It retains. It does not rebuild, and it does not re-accept.

## 2. The authority

The owner ruling is recorded on [#236][ruling]. It authorises exactly one
repository-controlled package:

```
ghcr.io/tesserafin-project/windows-ffmpeg-runtime
```

consumed only through an exact `sha256:` manifest digest, published only from
trusted `master`, private for the duration of W2–W4, and retained as one unit
carrying the runtime, its complete corresponding source, provenance, SBOM,
licences, checksums and the full acceptance evidence.

It authorises no release, no MSI, no server ZIP, no signature, no deletion
automation, no moving tag, and no start to W2.

## 3. The accepted identity

`ci/windows/runtime-retention/accepted-runtime.json` is the single authoritative
record. It is **measured** from the artifacts by `derive-accepted.py`, never
transcribed from a pull-request body, and it is validated against a closed
schema before any value in it is believed.

| | |
| --- | --- |
| platform | `win-x64` |
| accepted server commit | `83e23b9579404883c2d3e93f6f3ac8748061c618` |
| accepted server tree | `d66351490d0af8a8b4a31538d96bbd9eb58cb691` |
| W1-A3 proof head | `e608559fea49c8a23279678448f94aac15fe557d` |
| W1-A3 proof run | [32750491696] |
| FFmpeg upstream commit | `d4590e12452f94d40e413caecb34b08de608353b` |
| build revision | `7.1.4-tesserafin.1` |
| build inputs | `…/windows-ffmpeg-build-inputs@sha256:cff23b74…c04f0a` |
| runtime archive | `sha256:f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e` |
| corresponding source | `sha256:d753268c14d8e312bdd8ccd5ce8af90d495d185e807b77621e229eb8f71cc76d` |
| corresponding source, decompressed | `sha256:5158221a246c7e7d0d843d649571625ad0277152a093361419414195a8afee8e` |
| delivered paths | 31 |
| retained unit paths | 61 |
| topology | `dual-runner`, `sameNode=true`, `independenceClaim=none` |
| signed | `false` |
| expected OCI manifest digest | `sha256:99e45f154a5d72aba4185eb19b6671aa1a11c30be837deac9dd26f473593c0b9` |
| immutable tag | `accepted-83e23b957940` (never a consumer authority) |

## 4. What the evidence claims, and what it does not

Both runner allocations reported the **same node** (`runnervm6iq3x`). This is
dual-runner reproducibility between two isolated jobs on one runner-image
generation — it is **not** independence between two machines, and
`independenceClaim` says `none` for that reason.

What must nevertheless differ is the runner **allocation**: the two evidence
bundles record `GitHub Actions 1000008383` and `GitHub Actions 1000008384`. Two
bundles reporting one allocation would be a single build described as two, and
the schema validator refuses that case by name.

Both `runner.json` records travel inside the retained unit, so the limitation
travels with the artifact rather than living in a review comment.

## 5. Corresponding source is a retention condition, not an attachment

The build-input pattern has no equivalent of this section, because build inputs
are not a conveyed binary. A runtime is.

The runtime and its complete corresponding source are retained in **one** OCI
unit so that copying, mirroring or moving the artifact cannot separate them.
That pairing is enforced in code, in four independent places:

* `retention.make_layer` refuses to build a unit with a binary and no source;
* `contract.validate_accepted` refuses such a manifest **before** any count or
  digest check, so the refusal names the licence violation rather than an
  arithmetic disagreement about path counts;
* `consume.ps1` refuses to expose a runtime whose source is absent;
* control 20 proves all of the above by deleting the source and requiring the
  refusal.

The compressed archive **and** its decompressed stream are both pinned. A
`.tar.zst` whose container digest matches while its stream does not is exactly
the drift a container hash cannot see, and it is the difference between shipping
the corresponding source and shipping something shaped like it. Control 03
proves that check is load-bearing by substituting a *valid* archive with
different content.

### The public-availability gate

Private visibility is acceptable **only** while this package is an internal
W2–W4 build input.

> **Before any public 1.1 Windows binary built from this runtime is distributed,
> this package — including the complete corresponding source — must be publicly
> and anonymously available.**

Retaining the binary while the corresponding source is absent or unreachable is
not a permitted state at any point. Note that GHCR package visibility cannot be
changed through any REST or GraphQL route; it is a manual step, and it is a
release gate rather than a detail of this document.

## 6. The OCI unit

One layer, one config, one manifest. Layout:

```
delivered/…               the 31 accepted delivered paths, byte-unchanged
evidence/host-a/…         the complete host-a acceptance bundle
evidence/host-b/…         the complete host-b acceptance bundle
evidence/comparison.json  the dual-runner comparison record
RETENTION.md              the retention contract, generated deterministically
```

| | |
| --- | --- |
| artifactType | `application/vnd.tesserafin.windows-ffmpeg-runtime.v1+json` |
| config mediaType | `application/vnd.tesserafin.windows-ffmpeg-runtime.config.v1+json` |
| layer mediaType | `application/vnd.tesserafin.windows-ffmpeg-runtime.layer.v1.tar` |
| manifest mediaType | `application/vnd.oci.image.manifest.v1+json` |

Determinism is fixed here rather than left to a tool: sorted path order, USTAR
format with no PAX or GNU extension headers, uid/gid 0, empty owner and group,
mode 0644, `mtime = SOURCE_DATE_EPOCH = 0`, an **uncompressed** layer, canonical
JSON everywhere, and no `org.opencontainers.image.created` anywhere — the single
most common reason an "identical" artifact has two digests.

`RETENTION.md` deliberately does **not** print the manifest digest. It lives
inside the layer that digest is computed over, so a copy of the digest there
could not be written before the digest existed.

Publication uses `oras manifest push` and never `oras push`: the latter builds a
manifest of its own and would replace these bytes.

## 7. The gate does not expire

The expected OCI manifest digest is a **pure function of the committed
acceptance manifest**. `retention.expected_manifest_digest` reads no staged
file, no artifact and no registry, so the pull-request gate recomputes and
verifies the identity of a 260 MB unit from a few kilobytes of committed JSON.

This is deliberate, and it is the direct answer to W1-A3's O-1 observation. A
gate built on the proof run's artifacts would pass until 2026-09-23 and then
either fail for an unrelated reason or quietly stop checking. Everything the
digest cannot cover — determinism, the layer format, the consumer's refusals,
the registry protocol — is structural, and is exercised against a generated
fixture of the same shape by `make-fixture.py`.

## 8. Split workflows

| | validation | publication |
| --- | --- | --- |
| file | `w1-windows-runtime-retention.yml` | `w1-windows-runtime-publish.yml` |
| trigger | `pull_request` + path filters | `workflow_dispatch`, **no inputs** |
| permissions | `contents: read`, never raised | `contents: read`; `packages: write` on the publish job only |
| registry login | none | `GITHUB_TOKEN` only, no PAT, no repository secret |
| can publish | **no**, asserted by its own job | yes, from `refs/heads/master` only |

The trusted-ref check is a **step**, not a job-level `if:`. A job-level `if:`
that evaluates false reports SUCCESS, which would make a refused publication
indistinguishable from a completed one.

The publisher takes no caller-selectable package, digest, run id or tag. Even
the proof run it downloads artifacts from is read out of the committed
acceptance manifest. `consume.ps1` likewise has no `-Reference` parameter and no
environment-variable fallback, and no registry override — an override is a
caller-supplied redirect to another registry, which is a caller-supplied
reference wearing a different name.

## 9. Controls

`negative-controls.py` runs twenty controls and grades each **RED** (refused,
naming the property under test), **INERT** (failed for a setup reason, or
refused for the wrong reason) or **GREEN** (the mutation was accepted).

INERT is a failure of the *control*, not of the contract, and is never counted
as evidence. Twenty controls that all failed on an ImportError look identical to
twenty that work.

Current state: **20 RED, 0 INERT, 0 GREEN**, fixture restored byte-identically.

`registry-controls.sh` adds fourteen more against a local registry over plain
HTTP, with no credential anywhere: push, byte-for-byte read-back, idempotent
re-push, idempotent re-tag, the refusal to repoint an immutable tag, the proof
that the tag still resolves where it did after that refusal, and — added in R1 —
the refusal to write to a non-loopback registry without `--allow-remote`.

That suite earned its keep. It caught a real defect in `require_digest_reference`:
the shell guard used `[^@]+` for the repository path, which permits a colon, so
`host:5000/owner/name:sometag@sha256:…` matched and a reference carrying **both**
a tag and a digest was accepted. The Python contract refuses that case by name;
the shell one silently did not. Two independent statements of one rule are only
worth having if they actually agree.

## 9b. What W1-A4-R1 repaired

The R0 independent review raised two blocking findings and four bounded
observations. What follows is what changed, and what deliberately did not.

### F1 — the cannot-publish gate was a regex, not a permission model

`assert-cannot-publish.sh` interpreted permissions with six greps. That misses
`permissions: write-all`, which grants `packages: write` without containing the
string; it reads a quoted `"packages": "write"` and an inline
`{ packages: write }` differently from the plain form; it cannot see a job-level
block widening what the workflow level narrowed; and "this workflow contains no
push" is a statement about the workflow's own text, not about the scripts it
runs.

The interpretation now lives in `publication_policy.py`, which parses the file
with PyYAML — guaranteed by an explicit step in the validation workflow rather
than assumed of the runner image — and evaluates:

| property | refused |
| --- | --- |
| `permissions.workflow-level-not-exact` | anything but exactly `contents: read` at workflow level |
| `permissions.scalar-write-all` / `scalar-read-all` | either scalar form |
| `permissions.packages-write` | `packages: write` in any quoting, casing or mapping style |
| `permissions.write-scope` | every other write permission |
| `permissions.job-widens-workflow` | a job granting what the workflow level does not |
| `permissions.deployment-environment` | a job `environment:` |
| `credential.github-token` / `credential.secrets-*` | `${{ github.token }}`, `${{ secrets.GITHUB_TOKEN }}`, any `${{ secrets.* }}` |
| `registry.login` / `registry.credential-to-client` | a registry login, `--password-stdin` |
| `registry.write-verb-inline` / `registry.write-verb-in-helper` | a push, copy or tag outside the two `local-registry-protocol` files |
| `registry.allow-remote` / `registry.non-loopback-target` | lifting the loopback restriction, or naming a remote registry on a registry command line |

**The discriminator is authority, not the verb `push`.** The validation workflow
legitimately pushes — to `localhost:5000`, over plain HTTP, through
`registry-controls.sh`. A rule keyed on the token `push` would reject the very
workflow it is meant to accept, and the only way out of that is to keep weakening
it until it catches nothing. So `oci-protocol.sh` now refuses to write to any
non-loopback host unless handed `--allow-remote`; that flag appears in the
publication workflow and nowhere else; and the policy refuses any workflow that
passes it. `registry-controls.sh` proves the guard by offering it `ghcr.io` and
requiring the refusal.

`permission-fixtures.py` runs twelve reviewer fixtures, each required to reach
**one named property** — a fixture that is refused for a different reason is
INERT, not RED. Fixture 11 requires the pristine validation workflow to be
ACCEPTED, so a checker that refuses everything fails the suite; fixture 12
requires the real publication workflow to be refused, for the exact capability it
intentionally owns.

The checker's own source and its fixtures are excluded from the closure it
scans. That is the same trick the original grep gate needed, one level up:
`permission-fixtures.py` constructs a `${{ secrets.GITHUB_TOKEN }}` on purpose so
the checker can be shown refusing it, and reading that as the workflow's
capability is the absence of a thing reported as its presence. Fixtures 08, 09
and 10 put the same text in an inline `run:` body, in a helper, and in a helper
reached only through another helper — all three are refused, so the exemption is
narrow rather than a hole.

### F2 — nothing stopped a build input from hiding behind the exclusion

`boundary.py` states a **closed role schema** for `ci/windows/runtime-retention/**`.
Every file git would carry under that subtree must have exactly one permitted
retention role, and the inventory must not name a file that is gone. Roles are
retention concerns only: accepted manifest, accepted schema, deterministic OCI
assembly, retained-unit verification, digest-only consumer, publication-boundary
validation, local registry protocol, tests and fixtures, retention
documentation. Component source, patches, configure flags, toolchain locks,
dependency prefixes and build/package/acceptance scripts are named and forbidden
by name, so a hostile control's message says which build role was attempted
rather than only "unknown".

It also scans the W1 build workflow and its **transitive local script closure**
(thirty files) and requires that none of them reads from the subtree, that no
broad `ci/windows` glob can ingest it, that no build manifest names a path under
it, and that no environment variable redirects a build input there.

The inventory comes from `git ls-files --cached --others --exclude-standard`,
never a filesystem walk: `__pycache__/` is ignored-but-present on any tree where
these scripts have run, and a walk would report a pristine checkout as carrying
unclassified content.

`boundary-controls.py` runs seven hostile controls — a `runtime-retention/ffmpeg/build.sh`,
a toolchain lock, a W1 script sourcing a permitted retention file, the W1
workflow copying the subtree into its build context, a broad recursive glob, a
legitimate classified fixture that must be ACCEPTED, and the gate itself removed
from the validation workflow.

### The path-filter division, checked against GitHub's own semantics

| | W1 dual-runner build | retention validation |
| --- | --- | --- |
| `ci/windows/runtime-retention/**` | **excluded** | **included, and policed** |
| `ci/windows/ffmpeg/**`, `ci/ffmpeg/**` | triggers | not watched |
| own workflow file | triggers | triggers |

`pathfilter.py` implements GitHub's rules rather than approximating them: `*`
never crosses `/`, `**` does, `paths:` is an allowlist so a file starts excluded,
patterns are evaluated in order and the **last** match decides, and the workflow
runs if any changed file ends up included. That last-match-wins rule is what a
substring approximation gets wrong, and it is the rule the mixed diff depends
on — a pull request touching both the subtree and `ci/windows/ffmpeg/**` still
triggers the build, because the ffmpeg file is included on its own.

Fourteen cases are checked, including deletion and rename across the boundary and
a sibling (`ci/windows/runtime-retention-notes.md`) that merely starts with the
subtree name and must NOT be excluded.

### One reference grammar, three languages

`consume.ps1` still carried the permissive `[^@]+` repository shape that
`oci-protocol.sh` had already been repaired for, and PowerShell's `-notmatch` is
case-insensitive, so it also accepted an uppercase digest the other two refuse.
All three now classify in the same order, emit the same reason tokens, and are
run over one corpus by `reference-corpus.py`, which requires the same verdict
**and** the same reason. A parser that refuses the right reference for the wrong
reason is a disagreement, not a pass — which is precisely what leaning on the
later canonical-package equality check to repair an over-permissive grammar
would look like.

The canonical-package authority is deliberately not part of the grammar:
`localhost:5000/...` is a well-formed reference the local-registry controls need,
and only `contract.parse_reference` and `consume.ps1` add the "and it must be OUR
package" rule on top. The corpus checks that separately, against the two parsers
that carry it.

The PowerShell leg is not optional. Without `pwsh` the corpus refuses to report a
result rather than reporting two-thirds of it as a pass.

### Publication binds a revision, not only a ref

Asserting the repository and `refs/heads/master` is true of a checkout of **any**
commit on master, including a detached or substituted one. The publication job
now also asserts that the event is `workflow_dispatch`, that the checked-out HEAD
equals `GITHUB_SHA`, that `GITHUB_SHA` descends from the accepted W1-A3 merge
`83e23b9579404883c2d3e93f6f3ac8748061c618` (with `fetch-depth: 0`, because a
depth-1 checkout does not hold that object), and that the acceptance manifest on
disk is byte-identical to the one `git show <GITHUB_SHA>:…` returns — checked
once before the layout is built and again immediately before the push.

Master is **not** required to stand still at the W1-A4 merge. The durable
authority is the trusted master content that is checked out plus the exact digest
committed in it.

### Controls 09, 10 and 11 graded on a shared token

All three reached RED through the substring `predicts`, which is the generic OCI
digest recomputation message. Any of the three mutations produces it, so none of
them proved its own property: remove the gate control 10 is named for and control
10 still goes RED, through control 09's mechanism.

Each now has a unique sentinel, a mutation that is re-read from disk to prove it
landed, and a distinct assertion:

| control | mutation | assertion |
| --- | --- | --- |
| 09 | `proofHead`, `proofRun` | `IDENTITY-PROOF-HEAD-DISAGREES` — the manifest disagrees with the comparison record the proof itself wrote |
| 10 | `buildInputsReference` | `IDENTITY-BUILD-INPUTS-DISAGREE` — the same record, a different field |
| 11 | `acceptedServerTree` | the value is embedded in `RETENTION.md`, whose digest is pinned in the unit inventory, so the unit stops matching its own manifest |

`negative-controls.py --ablate` runs the three mutations against the three
expectations as a matrix and requires the off-diagonal to be empty, plus a `none`
row proving no control is satisfied by a pristine tree.

### Accepted limitations, recorded rather than repaired

These are properties of the ACCEPTED W1-A3 evidence. Editing the retained bytes
to remove them cosmetically would invalidate the digest that is the whole point
of retention.

* the retained evidence contains deterministic local Windows paths;
* the SBOM carries a deterministic timestamp;
* the dual-runner topology is same-node — two runner allocations, one physical
  node, named that way everywhere rather than claimed as two-host independence.

### Package absence is no longer a load-bearing premise

The author observed that `ghcr.io/tesserafin-project/windows-ffmpeg-runtime` did
not exist, with a suitably scoped token. The independent reviewer could not
reproduce that observation with theirs — `GET /orgs/.../packages` answers `403`
without `read:packages`, and the per-package route answers `404` for both
"absent" and "not visible to this token", so the two are indistinguishable from
outside.

W1-A5 must re-check package and tag state using authorised `read:packages`
access. Publication remains safe either way: `oci-protocol.sh tag` refuses to
repoint an immutable tag, and the push is by digest, so a conflicting
tag or digest is refused rather than overwritten, and a matching one is an
idempotent no-op.

## 10. Update policy

A retained unit is **never mutated**. Any change to the retained bytes produces a
different manifest digest, which is a different identity, recorded by re-deriving
`accepted-runtime.json`. Consumers move by editing the digest they pin.

Old digests are retained rather than deleted: a released runtime's corresponding
source and provenance point at what it was actually built from, and deleting
that digest would break the claim retrospectively. **No deletion or cleanup
automation is authorised.**

The immutable tag is never repointed. If it already resolves to the reviewed
digest, publication is an idempotent no-op; if it resolves elsewhere, publication
is refused.

## 11. Operational deadline

The proof run's artifacts expire **2026-09-23**. W1-A5 must publish before then,
or the accepted bytes become unrecoverable and W1-A3 would have to be re-run —
which would produce a new runtime that nobody has reviewed.

[#236]: https://github.com/tesserafin-project/tesserafin/issues/236
[ruling]: https://github.com/tesserafin-project/tesserafin/issues/236#issuecomment-5409680727
[32750491696]: https://github.com/tesserafin-project/tesserafin/actions/runs/32750491696
