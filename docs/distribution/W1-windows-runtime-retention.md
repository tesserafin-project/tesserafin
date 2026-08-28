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

The original owner ruling is recorded on [#236][ruling]. It authorises exactly
one repository-controlled package:

```
ghcr.io/tesserafin-project/windows-ffmpeg-runtime
```

consumed only through an exact `sha256:` manifest digest, published only from
trusted `master`, private for the duration of W2–W4, and retained as one unit
carrying the runtime, its complete corresponding source, provenance, SBOM,
licences, checksums and the full acceptance evidence.

It authorises no release, no MSI, no server ZIP, no signature, no deletion
automation, no moving tag, and no start to W2.

### The visibility element is superseded

That paragraph is quoted as written, because it is what the ruling said. Its
**visibility** element — and only that element — has since been superseded by a
second owner ruling on [#236][visibility-ruling]: the package is **public** for
the duration of W2–W4, and that is the intended state rather than a deviation
to be corrected.

Every other element of the original ruling stands unchanged: one package, digest
only, trusted `master` only, one unit, and no release, MSI, ZIP, signature,
deletion automation, moving tag or start to W2.

The reasoning is recorded on the ruling comment and summarised here because it
changes what a reader should verify. The source repository is public, so GHCR's
inherited default made the package public at creation; the accepted digest and
the whole build contract are already public; the unit carries the binary and its
complete corresponding source together, so anonymous availability **over**‑satisfies
the `GPL-3.0-or-later` condition rather than weakening anything; and no secret or
unreleased proprietary payload was ever being protected by private visibility.
Keeping the inherited state also removes a later manual visibility transition,
which would otherwise be an unautomatable manual step standing between W4 and a
release.

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

### The public-availability gate — satisfied on 2026-08-28

The condition itself is unchanged, and it is the one that matters:

> **Before any public 1.1 Windows binary built from this runtime is distributed,
> this package — including the complete corresponding source — must be publicly
> and anonymously available.**

Retaining the binary while the corresponding source is absent or unreachable is
not a permitted state at any point.

What changed is that this is no longer outstanding. **W1-A5 satisfied it at
first publication**, and it was never in the state this section previously
anticipated. The package has been public and anonymously readable from the
moment the accepted digest existed in the registry; there was never an interval
during which the retained binary was reachable and its corresponding source was
not.

| | |
| --- | --- |
| publication run | [33178349029] |
| accepted digest | `sha256:99e45f154a5d72aba4185eb19b6671aa1a11c30be837deac9dd26f473593c0b9` |
| immutable tag | `accepted-83e23b957940` → the same digest |
| observed on | 2026-08-28 |
| durable record | [#236][a5-record] |

With no credential at all, `tags/list` and the digest-addressed manifest return
`200`, the manifest bytes rehash to the accepted digest, and the layer blob
redirects to storage and downloads. Because runtime and complete corresponding
source are **one layer in one unit**, that single anonymous pull delivers both;
they cannot be separated by a mirror, a copy or a partial fetch. Both were
independently pulled anonymously and rehashed against `accepted-runtime.json`.

Public visibility is intended throughout W2–W4 ([the superseding
ruling](#the-visibility-element-is-superseded)). **No later manual visibility
transition remains outstanding**, and there is no residual release step of that
kind between W4 and a distributed binary.

Two properties of this gate survive that, and both are load-bearing.

**Nothing in this repository sets or proves visibility.** GHCR package
visibility cannot be changed through any REST or GraphQL route, so no workflow
here — the publication workflow included — can set it, and none claims to. The
publication workflow's summary says so explicitly and asserts nothing about the
current state in either direction. Visibility is an independently observed
property of the registry, and this document records an observation, not a
control.

**Release validation must re-measure, not cite.** A historical visibility claim
— including this one — does not discharge the gate above. Before any public 1.1
Windows binary is distributed, anonymous availability of both the runtime and
its complete corresponding source has to be measured against the registry *at
that time*. That is the only form of evidence the licence condition actually
needs, and it is why the paragraph above is dated and sourced rather than
written as a standing property.

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

### The unit's own `RETENTION.md` is frozen, including one stale conditional

`assemble.py` generates the in-unit `RETENTION.md` with pre-publication
conditional wording, beginning *"While this package is private…"*. That
sentence was written before anything was published and describes a condition,
not an observed state.

**It is deliberately not corrected, and it must not be.** That file is one of
the 61 paths in the accepted unit. It is inside the single layer, the layer
digest is inside the manifest, and the manifest digest is the accepted
identity. Changing one byte of it — or of `assemble.py`, which generates it —
produces a different layer digest, a different manifest digest and therefore a
**different OCI identity**, discarding the reviewed and independently verified
one. Rewriting frozen text to make it read better is not worth re-accepting a
260 MB unit, and the acceptance evidence has to remain exactly the bytes that
were accepted.

So the wording stays frozen as acceptance evidence, and the record is placed
outside the unit rather than inside it:

* the condition that sentence describes — public, anonymous availability of the
  runtime together with its complete corresponding source — **was satisfied by
  W1-A5**, at first publication, as recorded in [§5](#the-public-availability-gate--satisfied-on-2026-08-28);
* this document and the durable [#236][a5-record] record carry the observed
  post-publication truth;
* a reader who pulls the unit and finds that conditional inside it should read
  it as frozen pre-publication text, and read the current state here.

The same rule that makes the unit trustworthy is the rule that makes this
sentence unfixable. That is the intended trade.

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

`registry-controls.sh` adds **eighteen** more against a local registry over
plain HTTP, with no credential anywhere: push, byte-for-byte read-back,
idempotent re-push, idempotent re-tag, the refusal to repoint an immutable tag,
the proof that the tag still resolves where it did after that refusal, and —
added in R1 and extended in R2 — the refusal to write to a non-loopback
registry, to a remote IPv6 literal, to an unbracketed IPv6 authority, to an
authority carrying embedded credentials and to a hostname that merely ends in
`localhost`, in each case without `--allow-remote`.

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

`boundary-controls.py` runs thirteen hostile controls — a
`runtime-retention/ffmpeg/build.sh`, a toolchain lock, a W1 script sourcing a
permitted retention file, the W1 workflow copying the subtree into its build
context, a broad recursive glob, a misclassified file, the five symlink cases of
R2's finding D4, and two legitimate classified fixtures that must be ACCEPTED.

### The proof trigger is positive-only, and every retention change crosses it

| | W1 dual-runner build | retention validation |
| --- | --- | --- |
| `ci/windows/runtime-retention/**` | **triggers**, via `ci/windows/**` | **included, and policed** |
| `ci/windows/ffmpeg/**`, `ci/ffmpeg/**` | triggers | not watched |
| own workflow file | triggers | triggers |
| retention documentation | not watched | triggers |

R1 subtracted the retention subtree from the W1 build's `paths:` filter with an
ordered negation, and `pathfilter.py` existed to prove the negation subtracted
exactly that subtree. Independent review withdrew the optimisation (W1-A4-R1,
finding D2): the negation was safe only under a premise that cannot be
discharged — that a static pattern set recognises every way shell tooling can
stage a directory into a build. It cannot, and where it failed the exclusion
guaranteed that no proof ran over the ingested bytes.

The W1 `paths:` filter therefore carries **no negative pattern**, at any
position, for any subtree, and `pathfilter.py` is deleted rather than kept as
evidence for a policy that no longer exists. `trigger_policy.py` states the
opposite contract and is a roster gate:

* the filter carries no negation, so the optimisation cannot be reintroduced by
  a later edit without failing this gate;
* every representative retention change resolves the trigger TRUE — nested
  additions, single-file diffs, renames into, out of and within the subtree,
  deletions, and the sibling `ci/windows/runtime-retention-notes.md` whose name
  merely starts with the subtree name;
* the exact R1 staging diffs that made the old exclusion unsafe — a `cp -r
  ci/windows dst`, a `tar` of the parent directory, an `rsync` whose
  `--exclude` spelling differs, a `Path().rglob()` walk, and the retention file
  such a stage would ingest — all now resolve TRUE;
* every build-affecting change still resolves it true, as before.

The glob engine is retained because the positive claim needs it: "this diff
triggers the proof" is a statement about GitHub's matching rules — `*` never
crosses `/`, `**` does, `paths:` is an allowlist — not about whether a substring
appears in a string. What is gone is the claim it used to carry. This document
no longer asserts that any pattern set recognises every staging syntax; the
contract is that no pattern set has to, because nothing is subtracted.

The cost is accepted and stated: a retention-only pull request now starts the
~49-minute dual-runner build. That is the price of never having ingested bytes
go unproven.

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

## 9c. What W1-A4-R2 repaired

The R1 independent review raised four blocking findings and two low ones. What
follows is what changed.

### D1 — the cannot-publish closure was one file, not the reachable graph

`assert-cannot-publish.sh` and `publication_policy.py` parsed the validation
workflow's own permissions, credentials and `run:` closure, and stopped there. A
`uses: ./.github/workflows/x.yml` edge left the called workflow unread, so a
local reusable workflow carrying `permissions: {packages: write}`, an `oras
login` and an `oras manifest push`, invoked with `secrets: inherit`, resolved to
a workflow this gate called read-only.

The closure is now the **reachable workflow graph**. Every `uses:` edge to a
repository-local workflow is followed recursively; each node is parsed once and
memoised, so a cycle terminates instead of hiding the nodes behind it. At every
node the permission set is resolved semantically — `write-all`, a quoted or
inline mapping, an absent workflow-level block that a called workflow would
inherit, and any write scope including `id-token` — and every credential is
refused by name: `secrets: inherit`, an explicit `secrets:` mapping,
`${{ secrets.* }}` and `${{ github.token }}`. An edge that cannot be resolved to
a repository-local, `on: workflow_call` regular file inside `.github/workflows/`
is itself a refusal: an external `owner/repo/.github/workflows/x.yml@ref`, an
absolute `uses:` path, a `../` traversal, a symlinked workflow file and a callee
that is not a `workflow_call` each fail by their own name rather than being
skipped.

`reusable-workflow-controls.py` is the roster proof: sixteen controls, one per
refusal, plus a pristine read-only local reusable workflow that must be
ACCEPTED — a checker that refuses every graph proves nothing — and a duplicate
alias whose second edge must still be checked. It restores the reviewed
workflow byte-identically.

### D2 — the negative path filter is withdrawn, not narrowed

See *The proof trigger is positive-only* above. `pathfilter.py` and its
dedicated proof are deleted; the authority that replaced them is
`trigger_policy.py`, a roster gate. No replacement negative glob exists in any
workflow in this repository.

### D3 — the gate invocation was proved by substring presence

The validation workflow invoked seven gates as seven steps, and
`boundary.check_gate_is_pinned` proved they ran by testing whether the strings
`boundary.py` and `boundary-controls.py` appeared anywhere in the workflow file.
A **comment** naming either satisfied that. An invocation could be commented out
and the workflow still read as before, ran nothing, and reported the gate as
pinned.

There is now **one canonical command** — `retention_gates.py --validate` —
holding a closed roster resolved by exact module and function identity. A roster
entry that is deleted, duplicated, unknown, repointed at a missing function or
repointed at a no-op is refused by name.

The pin lives **outside the subtree it pins**, in
`ci/windows/verify-retention-gate-pinned.py`, which `ci/run.sh` runs
unconditionally and which is not skippable by flag or environment variable — a
pin the pinned code performs disappears the moment that code is deleted. It
parses the workflow as YAML and requires the exact job, that exact command as an
**active `run` value**, no `continue-on-error`, no unreachable step or job
condition and no success masking. Commenting the line out, prefixing it with
`:` or `echo`, appending `|| true` or `; true`, moving it to another job or
deleting the job each fail that check by their own name.

`gate-roster-controls.py` replayed all of it: seven closed-roster controls and
eleven structural-pin controls, each RED for its own reason, with a pristine
workflow that must be ACCEPTED and a byte-identical restoration.

**Superseded by R3.** The R3 review found that both halves of the paragraph
above were still a file deciding a question about itself: "an active `run`
value" was decided by a list of no-op shapes, and the closed roster named its
own required members. Section 9e states what replaced them. Read that section,
not this one, for the current contract.

### D4 — a role was read before the file type was checked

`boundary.py` classified every tracked path under the subtree by role and
extension, and a **symlink** carrying a permitted extension satisfied a role.
Replacing `reference-corpus.json` with a tracked symlink to
`ci/windows/ffmpeg/pe.py` therefore passed: the subtree could name a build input
by reference while every check reported a closed retention role schema.

Every tracked path under the subtree is now required to be a **regular file**,
checked before any role or content is read, from the Git index mode (`120000`)
and from `lstat`, never from a followed `open()`. `boundary-controls.py` covers
the reviewer's exact fixture, a symlink with a permitted extension **and** a
declared role, a relative symlink wholly inside the subtree, a dangling symlink,
a symlink staged in the index, and — as the necessary positive control — a
normal regular file that must be ACCEPTED.

### The two low findings

`oci-protocol.sh` and `publication_policy.py` disagreed about what a loopback
authority is, so a `[::1]:5000` reference was a permitted local registry to one
parser and a remote write to the other. One committed corpus,
`loopback-corpus.json`, now drives both: twenty-five authorities, each reaching
the **same named verdict** in Python and in shell — `localhost` with and
without a port, IPv4 loopback including `127.0.0.0/8`, bracketed and expanded
IPv6 loopback, an unclosed bracket, an unbracketed IPv6 literal, non-loopback
and unspecified IPv6, IPv6 spellings this grammar deliberately does not support,
`localhost.example.com` and `127.0.0.1.example.com` suffix tricks, a
`127.0.0.1`-prefixed name, embedded credentials, junk after the bracket, a
non-numeric port, a bracketed IPv4 and an empty host. Eight permit a registry
write; seventeen refuse one.

The trusted-source check's SHA-shape test was a boolean expression that could
not fail. It is now two independent properties, and
`trusted-source-controls.py` shows each is load-bearing alone: an empty, short,
long, abbreviated, uppercase, non-hexadecimal or `sha256:`-prefixed value is
refused naming `TRUSTED-SOURCE-SHA`, while a well-formed SHA whose checked-out
`HEAD` disagrees is refused naming `TRUSTED-SOURCE-HEAD`.

## 9e. What W1-A4-R3 repaired

Two findings, and they are the same shape twice: a file deciding a question
about itself.

### D3a — inertness was decided by a list of shapes

R1 proved the gate ran by searching the workflow's raw text for a filename,
which a comment satisfies. R2 replaced that with a YAML parse plus
`_NO_OP_PREFIX`, a success-mask regex and a substring containment test — a list
of the bypasses someone had thought of. R3 measured that list. **Eleven**
syntactically harmless wrappers were accepted by the R2 file as a live
invocation:

| accepted by R2 | accepted by R2 |
| --- | --- |
| `#cmd` — no space after the hash | `(cmd)` |
| `##cmd` | a block scalar containing the command |
| `cmd \|\| :` | a scalar padded with whitespace |
| `cmd \|\| echo x` | `working-directory:` on the step |
| `cmd &` | `shell:` overridden to a shell that does not fail fast |
| `if false; then cmd; fi` | |

The question has therefore changed. Not *is this command inert*, which needs a
shell semantics model and loses to the next wrapper anyone writes, but *is this
string the string*, which needs `==`.

The gate step now carries `id: retention-gate-roster`, and
`verify-retention-gate-pinned.py::check_command` requires its `run` value to
equal

    python3 ci/windows/runtime-retention/retention_gates.py --validate

**byte for byte** — one plain YAML scalar, no wrapper, no composition, no
padding, no trailing newline. A deviation that would in fact still run the
command is refused too. (**R4 changed the string**: the interpreter is now
absolute, `/usr/bin/python3 …`. See section 9f, F1 — a bare `python3` is a PATH
lookup, and a PATH lookup is something a startup file can answer.) That is deliberate: a gate step is not a place for
expressive shell, and refusing `(cmd)` costs nothing anyone wants.

Around the step the contract is structural, and each part has its own finding
name: no `if` at step or job (`cmd.step-conditional`, `cmd.job-conditional` —
*any* condition, true ones included), no `continue-on-error`, no
`working-directory`, no `strategy:` matrix, no `env:` at workflow, job or step
level that could change which `python3` runs or which file it reads, `shell`
absent or exactly `bash` (which GitHub expands with `-eo pipefail`), and the
`pull_request` path filters that reach the job still present.

`_NO_OP_PREFIX` and the success-mask and unreachability regexes are **deleted**.
Nothing decides command identity by running a shell, and no rule depends on
spacing after `#`.

### D3b — the roster named its own required members

`retention_gates.py` carried `GATES` — what it has — beside `REQUIRED` — what
it must have. One edit deletes an entry and its requirement together, and the
run gets shorter and stays green. Nothing inside
`ci/windows/runtime-retention/` can close that, because whatever is added there
is deletable by the same edit.

The authoritative roster moved to `ci/windows/verify-retention-gate-pinned.py`,
outside the subtree, as an exact mapping rather than a set of names. **R4
superseded that location** — see section 9f, F3: a roster frozen in the verifier
is a roster the verifier can edit, which makes the orchestrator and the verifier
a two-party agreement. The table below is still the contract; what changed is
where it is written down and what authenticates it.

| # | gate id | module | callable | kind | argv | tier |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `accepted-contract` | `contract.py` | `check_all` | findings | — | gate |
| 2 | `deterministic-layout` | `retention.py` | `check_all` | findings | — | gate |
| 3 | `publication-policy` | `publication_policy.py` | `check_all` | findings | — | gate |
| 4 | `excluded-subtree-ownership` | `boundary.py` | `check_all` | findings | — | gate |
| 5 | `proof-trigger` | `trigger_policy.py` | `check_all` | findings | — | gate |
| 6 | `registry-authority` | `loopback-corpus.py` | `check_all` | findings | — | gate |
| 7 | `ownership-self-proof` | `boundary-controls.py` | `main` | exit-code | — | proof |
| 8 | `publication-self-proof` | `permission-fixtures.py` | `main` | exit-code | — | proof |
| 9 | `reusable-workflow-self-proof` | `reusable-workflow-controls.py` | `main` | exit-code | — | proof |
| 10 | `trusted-source-self-proof` | `trusted-source-controls.py` | `main` | exit-code | — | proof |
| 11 | `reference-grammar-self-proof` | `reference-corpus.py` | `main` | exit-code | `--allow-missing-pwsh` | proof |
| 12 | `hostile-controls-self-proof` | `negative-controls.py` | `main` | exit-code | `--fixture {fixture}` | proof |
| 13 | `hostile-controls-ablation` | `negative-controls.py` | `main` | exit-code | `--fixture {fixture} --ablate` | proof |
| 14 | `gate-roster-self-proof` | `gate-roster-controls.py` | `main` | exit-code | — | proof |

**Position is part of the contract.** An order-independent rule would let a
gate and a proof trade places while both sets stayed equal, and "no gate
substituted by a self-proof" is one of the properties this has to hold. The
argv is part of the identity too: `negative-controls.py::main` with and without
`--ablate` is two members, not one named twice.

At runtime the verifier resolves the orchestrator's **real bindings**, through
the orchestrator's own loader, and refuses:

* a missing member, an unknown member, a duplicate, wrong cardinality;
* an id whose module, callable, kind, argv or tier differs from the table;
* a roster presented in another order;
* a callable that is a lambda or an alias — caught by `__name__`;
* a callable re-exported from another file under the expected name — caught by
  `__code__.co_filename`.

What remains inside the subtree is `DIAGNOSTIC_EXPECTATION`, which `--list`
prints drift against and which **refuses nothing**. `validate_roster()` keeps
only claims this file is entitled to make about itself: no duplicate id, no
duplicate identity, no entry naming a callable that does not exist.

### The controls, and where the regress stops

`gate-roster-controls.py` is rewritten around both halves:

| suite | count | result |
| --- | --- | --- |
| command identity (C01–C24) | 24 | 23 RED + 1 PASS (C20, pristine) |
| roster authority (N01–N11, NX, A1) | 13 | 13 RED |
| roster authority, pristine (N12) | 1 | PASS |
| the contract's other direction (N14) | 1 | RED |
| the no-op tier control (N13) | 1 | RED |
| the trust-root ablation (A2) | 1 | RED |

C23 puts the pristine step first and a second step claiming the same id after
it, so `cmd.step-duplicated` is proved to fire rather than merely to exist. C24
gives the step an `env:` setting `PATH` under an otherwise unchanged command,
so the environment rule is proved load-bearing too — an unproved boundary is
the exact class of defect this whole repair is about.

**N14 runs the contract in the other direction.** Every other roster control
mutates the subtree and requires the external file to refuse. N14 narrows the
EXTERNAL expectation and leaves the orchestrator pristine, and requires a
refusal there too — otherwise "authority" would only mean "veto", and a quiet
edit to the verifier would be the way to drop a gate.

C01, C03, C04, C05, C06, C08, C09, C12, C13, C18 and C19 are exactly the eleven
wrappers R2 accepted, marked `[R2-BYPASS]` in the source. NX is the reviewer's
exact mutation — the ownership gate, its self-proof and the gate-roster
self-proof deleted from the roster *and* from the diagnostic copy — and it is
refused with all three identities named in one finding.

Two ablations settle where authority lives:

* **A1** neuters the orchestrator's own `validate_roster()` *and* deletes a
  member. The external contract refuses anyway. The orchestrator's self-check
  is therefore not load-bearing for the roster question.
* **A2** extracted `ci/run.sh`'s pin block verbatim between the
  `# >>> W1A4-PIN-BLOCK` / `# <<< W1A4-PIN-BLOCK` markers and ran the extracted
  text against a tree with the verifier removed. **R4 deleted it** — see
  section 9f, F4. Extracting a block and running it separately proves the block
  works; it proves nothing about whether `ci/run.sh` reaches it, and the
  reviewer's mutation (the intact block moved below `exit "$STATUS"`) passed
  A2 unchanged. It is replaced by twelve structural controls and five runs of
  the real script.

`ci/run.sh` is the trust root, and it says so in its own comment. Nothing pins
it in turn, and nothing should: a chain of scripts each pinning the next has no
last link, and adding one more file would move the same defect one directory
further out rather than close it. `ci/run.sh` is a merge gate for every branch,
it invokes the verifier unconditionally, and a non-zero exit there fails the
run. That is stated rather than implied.

### The reviewed candidate, and its measured delta

The R3 mission text carried two delta figures — 22 files / +3254 / −475 as the
current state, and 21 files / +3069 / −456 as the stale claim it replaced.
**Neither is reproducible.** Measured at the frozen candidate
`9daafb85ea64c79100995812c379d6c1e8bcce81`, tree
`3a0d84178ad0de92855172a56fe1072b0179000e`, against base
`83e23b9579404883c2d3e93f6f3ac8748061c618`:

| source | files | insertions | deletions |
| --- | --- | --- | --- |
| `git diff --shortstat 83e23b9579...9daafb85ea` | 34 | 9801 | 2 |
| GitHub pull-request API for #254 | 34 | 9801 | 2 |

The two agree, and no head on this branch matches either mission figure: the
only 21-file head is `d2cc109630` at +5185, and no head has any deletions but
the two in `.github/workflows/w1-windows-ffmpeg-runtime.yml`. The figures are
recorded here as measured rather than as asserted; no commit message, document
or pull-request body in this branch ever carried the stale numbers, so there
was nothing else to correct.

## 9f. What W1-A4-R4 repaired

Independent review (R3) returned five blocking findings. Three of them are one
shape: something that decided a property was itself downstream of the thing it
was deciding about.

### F1 — `BASH_ENV` could stop the command running while the step succeeded

GitHub expands `shell: bash` to `bash --noprofile --norc -eo pipefail {0}`, and
a non-interactive bash **sources `$BASH_ENV` before the first line of the
script**. So

```yaml
env:
  BASH_ENV: mal.sh      # mal.sh: python3() { return 0; }
```

at workflow, job or step scope made `mal.sh` run first, the shell function
answered to the command's name, the validator never executed, and the step
exited 0. Adding `BASH_ENV` to the forbidden-name list does not close this: any
check run by a shell this workflow configures is downstream of the same
sourcing.

**Two mechanisms, and the second does not depend on the first.**

1. **Runtime, at the highest precedence GitHub offers.** The gate step declares
   a closed neutralising `env:` — `BASH_ENV` and `ENV` at `/dev/null`,
   `PYTHONSTARTUP` at `/dev/null`, and `PYTHONPATH`, `PYTHONHOME`,
   `PYTHONEXECUTABLE`, `PYTHONSAFEPATH`, `PYTHONNOUSERSITE`, `PYTHONWARNINGS`,
   `LD_PRELOAD`, `LD_LIBRARY_PATH`, `VIRTUAL_ENV` and `CONDA_PREFIX` empty.
   Step `env:` overrides job and workflow `env:` and is applied to the process
   environment **before the shell is created**, so bash starts with `BASH_ENV`
   already pointing at an empty file and sources nothing — whatever a wider
   scope tried to set, and whether or not the structural rule caught it.
2. **Structural, run by no shell this workflow configures.**
   `ci/windows/verify-retention-gate-pinned.py` refuses `BASH_ENV`, `ENV`,
   `SHELLOPTS`, `BASHOPTS`, `BASH_XTRACEFD`, `PS4`, `PATH`, `LD_PRELOAD`,
   `LD_LIBRARY_PATH`, `VIRTUAL_ENV`, `CONDA_PREFIX` and the ten `PYTHON*`
   names at workflow scope, at job scope, at `defaults.run` scope and on every
   step of the job; it requires the neutralising block above verbatim on the
   gate step; and `ci/run.sh` launches it through `/usr/bin/env -i`, so it
   inherits nothing at all.

The interpreter is now **absolute**: the canonical command is

```
/usr/bin/python3 ci/windows/runtime-retention/retention_gates.py --validate
```

`PATH` is therefore not load-bearing for the gate, and no shell function or
alias can answer for `/usr/bin/python3`.

`PYTHONSAFEPATH` is neutralised to the **empty string, not `1`**. The
orchestrator does `import boundary`, which resolves through the script's own
directory; `PYTHONSAFEPATH=1` removes that directory and the validator would
fail to import. That is measured, not assumed.

`retention_gates.py` prints `W1A4-ROSTER-SENTINEL` on entry and on success. The
proof records that **the real validator reached a reviewer-visible sentinel**,
not that a wrapper returned zero.

**The reviewer's experiment, replayed as an execution.** Control `B1` runs
`bash --noprofile --norc -eo pipefail` twice with `BASH_ENV` pointing at a
startup file that writes a marker and defines `python3() { return 0; }`:

| case | command | startup file ran | validator ran | exit |
| --- | --- | --- | --- | --- |
| R3 (vulnerable) | `python3 …`, no neutralising env | yes | no | 0 |
| R4 (repaired) | `/usr/bin/python3 …`, step env overlay | no | yes (sentinel) | 0 |

The vulnerable case has to reproduce or the repaired case proves nothing; if it
does not, `B1` grades INERT rather than RED.

### F2 — `defaults.run` bypassed the step-only checks

`defaults: {run: {shell: sh}}` at workflow or job scope changed the shell the
canonical command ran under while the step stayed byte-identical, and `sh` is
not expanded with `-eo pipefail`. `defaults: {run: {working-directory: ci}}`
likewise moved the directory a repository-relative path resolves against.

The contract is now stated across **every scope that can change what runs**:
workflow, job, `defaults.run`, the target step, inherited environment, shell,
working directory, interpreter resolution, conditional execution and failure
masking.

| scope | shell | working-directory | interpreter env |
| --- | --- | --- | --- |
| workflow | absent, or exactly `bash` | absent | forbidden |
| job | absent, or exactly `bash` | absent | forbidden |
| `defaults.run` (either scope) | absent, or exactly `bash` | absent | n/a |
| gate step | **required**, exactly `bash` | **required absent** | **required, exact values** |
| any other step of the job | unconstrained | unconstrained | forbidden |

A `defaults.run` key this contract does not enumerate is refused rather than
ignored. The workflow is parsed with a loader that **raises on a duplicate
mapping key**, because GitHub rejects such a document: accepting a last-wins
local parse would mean deciding a workflow is safe from a document that would
never run.

### F3 — the roster was a two-party agreement

R3's attack: delete `excluded-subtree-ownership`, its self-proof and
`gate-roster-self-proof` from `retention_gates.py`, delete the same three from
the verifier's frozen `EXPECTED_ROSTER`, and every check agrees with every other
check. Both representations were mutable and both were editable in one commit.

There is now a **third, separately located, machine-enforced commitment**, and
it is not a fourth self-verifier:

| where | what it holds |
| --- | --- |
| `ci/windows/w1a4-roster-manifest.v1.json` | the canonical roster — id, module, callable, kind, argv, tier and position for all fourteen members |
| `ci/run.sh` → `W1A4_ROSTER_MANIFEST_SHA256` | the manifest's exact content digest |
| `ci/run.sh` → `W1A4_ROSTER_IDS` | the fourteen identities, in order |

`verify-retention-gate-pinned.py` **consumes** the manifest and **reads both
constants out of `ci/run.sh`**. It writes down no roster of its own, so there is
nothing in it to edit: `EXPECTED_ROSTER` is populated at run time by
`install_roster()` and is empty until the anchor authenticates it. The verifier
cannot redefine the expected roster *and* the value that authenticates it,
because it holds neither.

A digest mismatch is **recorded without stopping the identity comparison**, so
the failure names the members that went missing rather than only observing that
the file changed. `ci/run.sh` additionally checks the digest itself, inline, in
bash, before the verifier runs — so a stubbed verifier does not silently take
the manifest check with it.

The reviewer's exact mutation and its neighbours:

| control | mutation | verdict |
| --- | --- | --- |
| `X01` | the three members removed from `retention_gates.py` **and** from the external expected roster | RED — `anchor.manifest-digest` + `anchor.manifest-roster-drift`, naming all three identities |
| `X02` | the same, with the eleven survivors reordered | RED, naming all three |
| `X03` | both sides repointed at a matching no-op callable that really exists | RED |
| `X04` | matching tier/position substitution on both sides | RED |
| `X05` | the manifest edited, the pinned digest untouched | RED, naming all three |
| `X06` | the pinned digest removed from `ci/run.sh` | RED — `anchor.digest-unpinned` |
| `X07` | the pinned identity list removed | RED — `anchor.ids-unpinned` |
| `X08` | three identities quietly dropped from the pinned list | RED — `anchor.manifest-roster-drift` |
| `X09` | the manifest deleted | RED — `anchor.manifest-missing` |
| `X10` | a schema version the verifier does not understand, correctly repinned | RED — `anchor.manifest-schema` |
| `BY` | the verifier replaced by a file that only exits 0 | RED — it implements none of the authority's API |

### F4 — the `ci/run.sh` pin block was not proved reachable

The block sat after `STATUS=$?`, and the only thing that proved it ran was
control `A2`, which **extracted** the block and executed the extracted text.
Extracting a block proves the block works. The reviewer's mutation — the intact
block moved below `exit "$STATUS"` — passed `A2` unchanged.

The block is now the **first thing `ci/run.sh` executes after its prologue**. It
is top-level, unconditional, before the image build, before the test dispatch
and before any status aggregation, and it is fail-closed by `exit 1` rather than
by folding into `$STATUS` — which does not exist yet at that point, and which a
later stage reassigns.

**Structural** (`check_trust_root`, twelve controls). The rule is an allowlist,
not a list of relocations: only comments, blank lines, `set`, simple
assignments, `cd`, `source` and *closed* function definitions may precede the
block. A function still open where the block begins makes the block a function
body and is refused by name. Inside the block, `|| true`, `|| :`, `set +e`,
backgrounding, assignment to `STATUS`, a missing `exit 1`, a missing
`/usr/bin/env -i`, a missing absolute-interpreter invocation and a missing
sentinel are each their own finding.

**Dynamic** (five runs of the real script). `ci/run.sh` is executed with a
stubbed verifier, a stubbed artifact purge, stubbed host probes and a stubbed
`docker` on `PATH`, so a run costs a second and can only measure order and
reachability.

| control | expectation | result |
| --- | --- | --- |
| `D1` pristine | sentinel printed, verifier ran, non-zero exit, build never dispatched | RED |
| `D4` verifier deleted | reached, exited non-zero, build never dispatched | RED |
| `D5` manifest not authentic | reached, exited non-zero, build never dispatched | RED |
| `D2` block below the final `exit` | never ran, script still exited 0 | RED |
| `D3` block removed to an uninvoked fragment | never ran, script still exited 0 | RED |

`D2` and `D3` are what prove the structural mutations **reach their intended
property**: the relocated block really is unreachable, so refusing it
structurally is refusing something real.

Neither half alone is sufficient, and both are kept. Structural inspection
cannot tell whether the script would run; a dynamic run cannot tell whether a
block that is reachable today stays reachable tomorrow.

### The control ledger

Every control below is machine-graded on each run, with `--json` for the full
record. A mutation that fails to apply, a module that will not import, a
timeout or a syntax error grades **ERROR**, never RED; a mutation that is
refused for a property other than its own grades **INERT**; a mutation that is
accepted grades **GREEN**. Only RED and PASS are passes.

| suite | count | result |
| --- | --- | --- |
| command identity (`C01`–`C24`) | 24 | 23 RED + 1 PASS (`C20`, pristine) |
| execution root (`E01`–`E20`) | 20 | 20 RED |
| roster authority (`N01`–`N11`, `NX`, `A1`) | 13 | 13 RED |
| roster authority, pristine (`N12`) | 1 | PASS |
| the contract's other direction (`N14`) | 1 | RED |
| the no-op tier control (`N13`) | 1 | RED |
| the independent anchor (`X01`–`X10`) | 10 | 10 RED |
| the independent anchor, pristine (`X11`) | 1 | PASS |
| the trust boundary (`BX`, `BY`) | 2 | 1 PASS + 1 RED |
| trust-root reachability, structural (`RB01`–`RB11`) | 11 | 11 RED |
| trust-root reachability, pristine (`RB12`) | 1 | PASS |
| trust-root reachability, dynamic (`D1`–`D5`) | 5 | 5 RED |
| the bootstrap experiment (`B1`) | 1 | RED |
| **total** | **91** | **86 RED + 5 PASS, 0 INERT, 0 ERROR, 0 GREEN** |

`E01`–`E20` are the execution-root controls: `BASH_ENV` at step, job and
workflow scope; `ENV`; `PATH`; `PYTHONPATH` and `PYTHONSTARTUP`;
`defaults.run.shell` at workflow and at job scope; `defaults.run.working-directory`
at workflow and at job scope; the neutralising entry repointed, removed, and the
whole block removed; a name added beside the closed set; the step declaring no
shell; a relative interpreter; a duplicate `run:` key; the bypass on a sibling
step of the same job; and the two trigger paths that keep a change to the anchor
or to the trust root from landing without running the job.

Every control restores its fixtures byte-identically and the suite asserts a
pristine working tree — the retention workflow, `retention_gates.py`, `ci/run.sh`
and the canonical manifest are each compared byte-for-byte after the run.

The D1, D2, D4, reference-grammar, loopback, trusted-SHA, registry,
publication-policy, symlink and boundary controls of R1–R3 are retained
unchanged; none of their named properties was weakened.

### The trust boundary, and what it does not cover

Stated as measured, not as claimed. Control `BX` performs the three-file edit —
`retention_gates.py`, the canonical manifest **and** `ci/run.sh`'s pinned digest
and identity list, all consistent — and records that it is **not detected**.
That is the boundary:

* removing a roster member now requires an edit to the orchestrator, an edit to
  the canonical manifest, and an edit to `ci/run.sh`;
* `ci/run.sh` is the merge gate for every branch and is reviewed as one;
* nothing pins `ci/run.sh` in turn, and nothing should — a chain of files each
  pinning the next has no last link, and adding one more would move the same
  defect one directory further out.

`BX` grades INERT if the three-file edit *is* refused, because the documentation
would then be describing a narrower boundary than the implementation.

Two further limits are part of the record:

* the structural reachability rule reasons about `ci/run.sh` **as text**. It
  refuses anything it cannot show to be prologue, so it is fail-closed, but it
  is not a shell semantics model;
* the dynamic runs execute the real `ci/run.sh` with every heavy dependency
  stubbed. They prove order and reachability. They prove nothing about the
  build stage they deliberately never reach.

### What this section does not claim

* The exact-head reproducibility runs recorded in sections 9d and *The R2
  head's own exact-head runs* are evidence **for their own heads only**. They
  say nothing about the R3 head or the R4 head; each head that needs a
  dual-runner result gets its own run.
* Package absence at `ghcr.io/tesserafin-project/windows-ffmpeg-runtime`
  remains a **W1-A5 check** with authorised `read:packages` access. From
  outside, "absent" and "not visible to this token" are indistinguishable.
* The dual-runner topology is **same-node**: two runner allocations on one
  physical node. It is a determinism result, not a two-host independence claim.
* **W2 is still blocked.** W1-A4 is validation only; it publishes nothing, and
  no part of this repair changes that.

## 9d. The predecessor proof run

Run [32864950596] is **completed / success** at head
`2622dd442c5ce68f04c8c43ae1d66fd4163ffcde` — the W1-A4 head as it stood *before*
the R2 repair. It ran `2026-08-25T15:18:16Z → 16:07:19Z` (49m, displayed 49m01s)
on the `W1 Windows FFmpeg Runtime — Dual-runner proof` workflow, and all four
jobs succeeded:

| job | conclusion |
| --- | --- |
| Negative controls and policy gates | success |
| Build on a clean native Windows runner (a) | success |
| Build on a clean native Windows runner (b) | success |
| Dual-runner comparison | success |

Both runners produced **identical 31-path delivered inventories**, and the
digests are unchanged from the accepted manifest:

* runtime `f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e`;
* corresponding source `d753268c14d8e312bdd8ccd5ce8af90d495d185e807b77621e229eb8f71cc76d`;
* corresponding-source stream `5158221a246c7e7d0d843d649571625ad0277152a093361419414195a8afee8e`.

Two limits are part of the record, not footnotes to it. The dual-runner topology
is **same-node** — two runner allocations on one physical node — so this is a
determinism result, not a two-host independence claim. And the run carried **no
publication authority**: it is a `pull_request` run of a workflow with no
`packages: write` at any level, so nothing was pushed to GHCR.

Above all, this run is evidence for **the predecessor head only**. It proves
W1-A3 non-regression at `2622dd442c`; it says nothing about the R2 head, which
the R2 push starts a new dual-runner run against precisely because the negative
exclusion is gone.

### The R2 head's own exact-head runs

The R2 push produced two runs at exactly `9daafb85ea64c79100995812c379d6c1e8bcce81`,
and both are **completed / success**:

| run | workflow | window | conclusion |
| --- | --- | --- | --- |
| [32877243939] | W1 Windows FFmpeg Runtime — Dual-runner proof | 17:19:51Z → 18:06:00Z | success |
| [32877243948] | W1 Windows Runtime Retention | 17:19:51Z → 17:21:00Z | success |

[32877243939]: https://github.com/tesserafin-project/tesserafin/actions/runs/32877243939
[32877243948]: https://github.com/tesserafin-project/tesserafin/actions/runs/32877243948

Three required checks at that head are **SKIPPED, not PASS**, because heavy CI
in this repository is draft-gated and #254 is a draft: `Tests`,
`OpenAPI Check` and `ABI Compatibility`. They are recorded as skipped rather
than counted as green; a draft pull request cannot satisfy them, and readying
#254 is not part of W1-A4. `CodeQL`, `SDK Provenance` and `Dependency Audit`
all succeeded at that head.

Package absence at `ghcr.io/tesserafin-project/windows-ffmpeg-runtime` remains
a **W1-A5 check** with authorised `read:packages` access, for the reason given
in section 9b: from outside, "absent" and "not visible to this token" are
indistinguishable.

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

## 11. Operational deadline — met

The proof run's artifacts expire **2026-09-23**. Until publication, the accepted
bytes existed in exactly one place: that expiring artifact storage. Had the
deadline passed first, the bytes would have become unrecoverable and W1-A3 would
have had to be re-run — producing a new runtime that nobody had reviewed.

**W1-A5 published the accepted bytes on 2026-08-28**, in run [33178349029],
ahead of that expiry. The runtime, its complete corresponding source and the
full acceptance evidence are now retained as one digest-addressed unit in the
registry rather than only in artifact storage, so the 2026-09-23 date is no
longer a risk to the accepted identity.

The date itself remains true and is not withdrawn: the artifacts still expire
then, and the statements elsewhere in this document that the retention gate
does not depend on them ([§7](#7-the-gate-does-not-expire)) remain the reason
the gate keeps working afterwards.

[#236]: https://github.com/tesserafin-project/tesserafin/issues/236
[ruling]: https://github.com/tesserafin-project/tesserafin/issues/236#issuecomment-5409680727
[visibility-ruling]: https://github.com/tesserafin-project/tesserafin/issues/236#issuecomment-5454747674
[a5-record]: https://github.com/tesserafin-project/tesserafin/issues/236#issuecomment-5454486781
[33178349029]: https://github.com/tesserafin-project/tesserafin/actions/runs/33178349029
[32750491696]: https://github.com/tesserafin-project/tesserafin/actions/runs/32750491696
[32864950596]: https://github.com/tesserafin-project/tesserafin/actions/runs/32864950596
