# A7 — the server↔web release pair

Issue #93 / **[A7] Reconcile server/web contract pairing for release**.

This document records the immutable identities of one server↔web release pair,
the evidence produced against them, and — explicitly — what is still not proven.

Everything here was run **locally**. Hosted GitHub Actions were **not** restored
and did not execute. Actions remain parked for this repository (#62); restoring
enforced, off-laptop CI is #94 / [C1], and the hosted contract gate is
#97 / [C4]. A local gate is not an enforced CI gate and is never described as one.

---

## 1. The pair

**This is the pair A7 closes on.** The first pair proved every clause but one
(the client teardown was racy under navigation) and is recorded as superseded in
§8 — it is kept because the diagnosis that came out of it is the evidence that
`tesserafin-web#60` was a real client defect and not a server one.

| | |
|---|---|
| `SERVER_SOURCE_SHA` | `a39f28c6406ab4e444ad5bd1837f447faeafdb96` |
| `SERVER_IMAGE` | `ghcr.io/tesserafin-project/tesserafin@sha256:032afb13a54b50823a2acbf30465f4e1f63efa26dacd041cbf50ba11df44994a` |
| `SERVER_IMAGE_ARCH_DIGEST` (`linux/amd64`, the one executed) | `sha256:6ec5eb211b6cb24ef4a6853deaec0f5f69b1d8aa1e6c25c3ef81c355e4a177d9` |
| `linux/arm64` child (built and label-checked, **never booted**) | `sha256:21e2bb908138e4d4a1a794413084716ed14ad6906baabbee84aa4f10a0b9fed9` |
| readable tags for the same manifest | `12.0.0-dev.a39f28c6406a`, `sha-a39f28c6406ab4e444ad5bd1837f447faeafdb96` |
| `WEB_SOURCE_SHA` | `855e2e3c25de42cec292192149947309678f5250` |
| `WEB_ASSETS_IMAGE` | `ghcr.io/tesserafin-project/tesserafin-web-assets@sha256:8ff18f389ab1d0f8fbc3a311a080c377fda9bad766167becbd4855a75207b5dd` |
| `OPENAPI_HASH` (generated == committed) | `0700633b3c12ff04df694ea3d5e81be72cc07dc66b70a8e3048cae8363fabf66` |
| `WEB_PINNED_OPENAPI_HASH` | `49e4eb87735f9c69791d1962823b29f49cc6ec500ca71f8898cf4255e85d7482` |

The web-assets image is **single-platform `linux/amd64` by design** — the payload
is architecture-neutral static content, and the server `Dockerfile` consumes it
through a `FROM --platform=linux/amd64` stage pin so both server architectures
copy byte-identical web bytes. The validator asserts that claim against the
published manifest: **both** server children declare
`org.tesserafin.web.revision = 855e2e3c25de…`.

The two OpenAPI hashes differ **by construction and not by content**: the web
repository pins the contract after the SDK generator's normalisation pass
(`fixSchema()` plus a re-serialisation at two-space indent), so its bytes are a
deterministic derivative of the canonical document rather than a copy of it. What
must be equal — and is — is the canonical contract at the commit the pin names
and the canonical contract at `SERVER_SOURCE_SHA`. See §4.

The server image is identified **by manifest digest** everywhere evidence is
claimed. Tags are recorded for readability only and are never the evidence
identity.

---

## 2. Carry-forward dispositions (#43, #68, #69, #70, #71)

Established from ancestry and patch evidence against `origin/master`, not from
issue state.

| Item | Type | State | Landing commit | Ancestor of `master` | Notes |
|---|---|---|---|---|---|
| #43 | issue | CLOSED (completed 2026-07-20) | PR #47 `522416764b79592120095fbecac862451dc8fbc8`, PR #69 `e2ea9f63fd87525a996395bce1c85445a2a9007c` | yes | `PlaybackAttemptId` shared by a whole playback attempt. #47 introduced the id; #69 extended the scope to the bodyless `GET .../Stream` and `DELETE` legs, recovering the id from the stored session. Sub-issues #70 and #71 were split out of it. |
| #68 | PR | MERGED 2026-07-20 | `f0a61ba27cb7bda5e059e69b9f2f87e5ffd997c8` | yes | Restricted non-admin user seeded in `ci/serve-e2e.sh`, the server half of the web access-restriction fixture. |
| #69 | PR | MERGED 2026-07-20 | `e2ea9f63fd87525a996395bce1c85445a2a9007c` | yes | Lifecycle logging and GET/DELETE correlation. Additive: no `openapi/` change, no new request field, no new header. |
| #70 | issue | CLOSED (completed 2026-07-20) | PR #73 `5aa7081322b3d8cbf53954c1ccc3363cfeeb1f67`, PR #74 `b022efa8bc465078bc68774d47169f9317cb48af` | yes | "A `PUT` re-plan to Transcode leaves no executable v2 plan (`PlanNotExecutable`)". #73 demotes a constraint-forbidden method instead of vetoing the source; #74 stops a legacy segment fetch degrading a client-owned v2 plan and makes a non-executable authoritative re-plan fail atomically. |
| #71 | issue | CLOSED (completed 2026-07-20) | PR #72 `665eabd387096522ab66cab3b3b946469e96773a`, PR #74 `b022efa8bc465078bc68774d47169f9317cb48af` | yes | "A live v2 session disappears before the final `DELETE` (`already gone`)". #72 stops an ending ffmpeg job killing a live client-owned session. |

Current tests for these on `master`:

```
tests/Tesserafin.MediaEncoding.Tests/Playback/PlaybackSessionManagerPlanPreservationTests.cs
tests/Tesserafin.MediaEncoding.Tests/Playback/PlaybackSessionManagerV2PlanRetentionTests.cs
tests/Tesserafin.MediaEncoding.Tests/Playback/PlaybackSessionManagerLifecycleTests.cs
tests/Tesserafin.MediaEncoding.Tests/Playback/PlaybackSessionManagerRequestCorrelationTests.cs
```

### Web disposition

`tesserafin-project/tesserafin-web` PR **#47** ("re-plan the v2 session on retry
via PUT") merged as `a32acbb10cb4ef5de548db8ac07e6118865bbeca`, an ancestor of
`main`. Its implementation is present on `main`:

```
src/scripts/playbackSessionV2Url.ts / .test.ts
src/scripts/playbackSessionV2ReplanTrigger.ts / .test.ts
src/scripts/playbackSessionTeardown.ts / .test.ts
src/scripts/playbackSessionTeardownTrigger.ts / .test.ts
src/components/playback/playbackmanager.js
tests/e2e/playback-v2-lifecycle.spec.ts
tests/e2e/playback-v2-lifecycle-oracle.spec.ts
```

That PR's description is in French and still states that two **server** defects
block it — `PlanNotExecutable` after a successful re-plan, and a session
`already gone` at `DELETE`. Both were subsequently fixed by server PRs #72, #73
and #74, all merged before the web PR. The PR body is immutable history and is
**not** rewritten; the correct current disposition is this table.

### Old branch disposition

`w2-srv-plan-preserve-v2 @ 0fd5b853a1e52078f6c719d740933ded60646c34` **still
exists on the server remote**. It is inert history, not a dependency:

* its three commits changed exactly `Reefin.MediaEncoding/Playback/PlaybackSessionManager.cs`,
  `…/PlaybackSessionManagerPlanPreservationTests.cs` and
  `…/PlaybackSessionManagerV2PlanRetentionTests.cs`;
* that change set was squash-merged as PR #74 `b022efa8bc…`, and all three files
  exist on `master` under their post-rename `Tesserafin.*` paths;
* `0fd5b853` is deliberately **not** an ancestor of `master` — that is what a
  squash merge means, and is not evidence of missing work.

A full-text scan of both repositories for `w2-srv-plan-preserve-v2` and
`w2-web-43` returns **zero occurrences** — no runtime code, no CI, no script, no
documentation. No live release process depends on the branch name. It is left in
place rather than deleted; deleting it would destroy history without removing a
dependency that does not exist.

The related scan for `all3f0r1/reefin` / `all3f0r1/reefin-web` finds only:

| Location | Classification |
|---|---|
| `docs/pr76-audit-cloture-lookup-v1.md`, `docs/design-playback-v2-lifecycle.md`, `docs/pr116-client-migration-design.md`, `docs/major-rewrite-plan-v13.md` (server) | historical design documentation |
| `docs/local-ci.md` (both repos) | stale repository-facing documentation — the CI-restoration procedure still names the pre-rename slugs. Identity scope, tracked by #101; **not** an A7 dependency, and not rewritten here |
| `docs/tesserafin/bench-hdr/hdr-detection-feasibility.md` (web) | historical issue link |
| `tests/e2e/playback-v2-lifecycle-oracle.spec.ts` (web) | provenance comment naming issue #43 |
| `scripts/verify-no-new-reefin.mjs` (web) | CI dependency, deliberate: that script's own allow-list explicitly permits historical provenance mentions |

---

## 3. Image and bundled-web provenance

The image was built by `docker/build-clean.sh --target server --output push` from a
clean checkout at `99783b2a74`, with the tags derived by
`docker/version-contract.sh` and never typed by hand.

### Version surfaces — `docker/version-verify.sh`, invoked by the validator

```
== image under test: ghcr.io/tesserafin-project/tesserafin@sha256:e7551ac8… ==
  canonical version (SharedVersion.cs) : 12.0.0
  PASS  resolves to an immutable digest
  architecture : amd64
  PASS  org.opencontainers.image.version '12.0.0' == SharedVersion.cs
  PASS  org.opencontainers.image.revision is a full commit sha (99783b2a743ee62617c77ffb12046f788c229e1c)
  PASS  revision == expected source commit
  PASS  /health reached 200 status=healthy
  PASS  /health version '12.0.0' == org.opencontainers.image.version
  PASS  /health version == SharedVersion.cs
  PASS  application-reported version '12.0.0' == /health version
```

`SERVER_OCI_REVISION` = `99783b2a743ee62617c77ffb12046f788c229e1c` =
`SERVER_SOURCE_SHA`. `SERVER_VERSION` = `HEALTH_VERSION` = `12.0.0`.

### Bundled web — compared to the web repository, not only to itself

```
label  org.tesserafin.web.revision     : fa47bab7f09d635f0b79b0814ddff2a1a1108400
label  org.tesserafin.web.version      : 13.0.0
label  org.tesserafin.web.assets.image : ghcr.io/tesserafin-project/tesserafin-web-assets@sha256:357afd28…
image  /opt/tesserafin-web.revision.json revision : fa47bab7f09d635f0b79b0814ddff2a1a1108400
PASS  the OCI web revision label equals the in-image revision file
PASS  the bundled web revision equals the named --web-source
PASS  the named web commit exists in the web checkout
PASS  the image records a bundled web version (13.0.0)
PASS  the bundled web-assets image is pinned by manifest digest
PASS  the server source's Dockerfile pins the same web commit (WEB_VCS_REF)
```

The last two assertions are the ones that matter. An image whose label and
in-image file agree with each other proves nothing if both name the wrong tree, so
the revision is additionally required to equal a commit that **exists in a real
`tesserafin-web` checkout whose `HEAD` is that commit**, and the `Dockerfile` at
`SERVER_SOURCE_SHA` is required to declare the same `WEB_VCS_REF`.

### Architecture

```
arch: linux/amd64 sha256:0d31dadff3bbef76385157292f4a0aa3e44786ef448a599decba1fa5a529ef04
arch: linux/arm64 sha256:7b73b5225d2e77d88c0e9e9394d296ddc1e6cc14ad1d1898ff0b6305226b61f9
PASS  linux/amd64 declares the named bundled web revision
PASS  linux/arm64 declares the named bundled web revision
PASS  the manifest list carries 2 runnable architectures
```

The whole A7 gate was **executed on `linux/amd64` only**. The `linux/arm64` child
image was built, published and had its labels read; it was **not booted**, and no
functional or browser claim is made about it anywhere in this document.

---

## 4. OpenAPI and SDK pairing — Outcome A, zero contract drift

1. **Generated from `SERVER_SOURCE_SHA`.** `./ci/openapi-generate.sh` was run from
   a clean checkout at `99783b2a74`. It builds the server in a container and
   writes the canonical document from the running application.

   ```
   RESULT: PASS — contract regenerated
     openapi/openapi.json  sha256 0700633b3c12ff04df694ea3d5e81be72cc07dc66b70a8e3048cae8363fabf66
   ```

   `git status --porcelain` was **empty** afterwards: the regenerated contract is
   byte-identical to the committed one.

2. **Committed canonical contract**:
   `0700633b3c12ff04df694ea3d5e81be72cc07dc66b70a8e3048cae8363fabf66`, matching
   `openapi/contract.lock.json`.

3. **The contract the web pinned.** `src/lib/tesserafin-sdk/spec/version.json`
   records `sourceCommit = 8c358f930c2b903c81de6a28ab07e074fd88b3f5`.
   `git show 8c358f93:openapi/openapi.json | sha256sum` is
   `0700633b3c…` — identical to the release contract. No commit between
   `8c358f93` and `99783b2a` touches `openapi/`, so the contract has not moved.

4. **SDK regeneration.** `TESSERAFIN_SERVER_REPO=<server> npm run verify:tesserafin-sdk-fresh`:

   ```
   generated/ and spec/openapi.json match a fresh regeneration from the pinned spec.
   Pinned spec sha256 matches, provenance = 8c358f930c2b903c81de6a28ab07e074fd88b3f5.
   Pinned spec matches the canonical contract at 8c358f930c2b903c81de6a28ab07e074fd88b3f5 exactly.
   NOTE: pinned contract is 13 commit(s) behind origin/master (99783b2a…). Not a failure.
   PASS.
   ```

5. **Drift**: none. The web working tree was clean after the regeneration.

This is **Outcome A**. No SDK was regenerated for the sake of it, no provenance
metadata was rewritten, and `fa47bab7f0` remains the paired web revision. No web
source change was required, so no web PR was opened.

---

## 5. The validator

`ci/verify-release-pair.sh` is the single documented entry point.

```
ci/verify-release-pair.sh \
    --server-image ghcr.io/tesserafin-project/tesserafin@sha256:e7551ac881bab01c10f290103ca3905e2779fbc1da5110aa11a26b0364a9f1f8 \
    --server-source 99783b2a743ee62617c77ffb12046f788c229e1c \
    --web-repo ../tesserafin-web \
    --web-source fa47bab7f09d635f0b79b0814ddff2a1a1108400 \
    --lifecycle-runs 3
```

It is runnable from clean checkouts and does not read the caller's current
branch: it verifies that each checkout's `HEAD` **is** the commit it was named,
and aborts otherwise.

It fails closed. `--skip-e2e` and `--skip-openapi-regen` exist for iteration, and
both mark the run DEGRADED and force a non-zero exit — there is no flag that
turns a missing proof into a pass. `--keep` is the documented debug-preservation
option: on failure it retains the containers, the volumes, the synthesised media
fixtures, the server logs and the Playwright traces, and prints where they are.
Otherwise cleanup runs on success **and** on failure.

Failure output names the layer that disagreed: `ARGUMENTS`, `IMAGE PROVENANCE`,
`BUNDLED WEB PROVENANCE`, `OPENAPI`, `GENERATED SDK`, `BROWSER E2E`,
`LIFECYCLE CONTRACT`.

It does not re-implement A5/A6. `docker/version-verify.sh` and
`docker/browser-onboarding.sh` are invoked, not copied.

`ci/run.sh` deliberately does **not** call it: the cross-repo gate needs a
published image, a neighbouring web checkout and a browser, and requiring those
for ordinary server-only work would be wrong. `ci/run.sh` prints the command
instead.

---

## 6. Evidence

### The gate run

```
ci/verify-release-pair.sh \
    --server-image ghcr.io/tesserafin-project/tesserafin@sha256:e7551ac881bab01c10f290103ca3905e2779fbc1da5110aa11a26b0364a9f1f8 \
    --server-source 99783b2a743ee62617c77ffb12046f788c229e1c \
    --web-repo ../tesserafin-web \
    --web-source fa47bab7f09d635f0b79b0814ddff2a1a1108400 \
    --lifecycle-runs 3 --keep

RESULT: PASS — the release pair is proven
```

`--keep` preserves the containers, volumes, server logs and Playwright traces
**only on failure**; a passing run cleans up. The retained record of this passing
run is therefore its transcript, quoted below, not an on-disk work tree. To keep
the artefacts of a green run, re-run and stop the cleanup deliberately.

Layer results: `ARGUMENTS` PASS · `IMAGE PROVENANCE` PASS ·
`BUNDLED WEB PROVENANCE` PASS · `OPENAPI` PASS · `GENERATED SDK` PASS ·
`BROWSER E2E` PASS · `LIFECYCLE CONTRACT` PASS.

### Boot and identity — `docker/browser-onboarding.sh`, against the release image

19 assertions, 0 failures, on pristine volumes:

```
PASS  the final command does not contain --nowebclient
PASS  the final command hosts the bundled web client via --webdir
PASS  /opt/tesserafin-web/index.html is present in the image
PASS  paired tesserafin-web commit fa47bab7f09d635f0b79b0814ddff2a1a1108400 recorded
      in both label and image content
PASS  /config and /data are empty before first boot
PASS  / redirects to the web client, resolves to HTTP 200, HTML content type
PASS  / does not serve API documentation
PASS  / serves the Tesserafin Web production document
PASS  all critical scripts/styles referenced by index.html are served
PASS  onboarding is NOT marked complete before the browser test
PASS  browser completed the first-run wizard end to end
PASS  completed onboarding survives a restart
PASS  completed onboarding survives container recreation
PASS  read-only media mount still rejects writes after recreation
```

### Contract-critical playback — three consecutive rounds, each from clean state

Specs (from `tesserafin-web` @ `fa47bab7f0`, run against the release image by
digest with a real Chromium and real HTTP):

```
tests/e2e/playback-v2-lifecycle-oracle.spec.ts
tests/e2e/playback-attempt-id-contract.spec.ts
tests/e2e/playback-v2-server-contract.spec.ts
```

Each round creates a new container, new `/config`, `/data`, `/cache` volumes and
newly synthesised media fixtures, then onboards and seeds over the real public
API.

| Round | Result |
|---|---|
| 1 | **PASS** — 16 passed (47.8 s) |
| 2 | **PASS** — 16 passed (53.0 s) |
| 3 | **PASS** — 16 passed (45.2 s) |

What those rounds prove, from the oracle's own assertions: the media fixture is
indexed and visible; v2 session creation succeeds; the direct-play path returns
real media bytes; the re-planned path returns bytes that `ffprobe`/`ffmpeg` confirm
are **really transcoded** (an HLS/MPEG-TS container the source `.mp4` could not
have produced, with frames that genuinely decode); the retry re-plans through
`PUT Playback/Sessions/{id}`; one `PlaybackAttemptId` spans the whole attempt while
`X-Request-Id` differs per HTTP request; a successful authoritative re-plan leaves
an executable v2 plan; `served from legacy`, `PlanNotExecutable` and `already gone`
are absent from the server's own log while a positive control proves that log was
live; the session is proven live from outside the browser immediately before the
stop; the final `DELETE` is recorded by the server as `deleted`, and the session is
genuinely 404 afterwards; a late `DELETE` naming a dead session does not take a
newer attempt down with it; and an authoritative re-plan with no viable plan fails
atomically (422) leaving the previous executable session intact.

### Runs that failed, and why they are on the record

Nothing below was re-rolled away.

**Rehearsal run 0 — FAILED.** Before the gate existed, the oracle was run by hand
against the A6 image `…@sha256:29493c0e…` to rehearse the rig. It failed stage 8:
the client's teardown `DELETE` never arrived within 60 s, so the session was still
serving (200, expected 404). An identical clean re-run passed in 18.6 s.

That image pins the **same** web-assets digest and the same `WEB_VCS_REF` as the
release image, and the `Dockerfile` is byte-identical between the two commits — so
this is evidence about the same client code, not about a different subject. The
server behaved correctly throughout (it retained the client-owned session across
`TranscodingJobEnded` and waited for an explicit `DELETE`, exactly as #72/#74
require); the request was never sent. Filed as
`tesserafin-project/tesserafin-web#60`.

> **Correction, established when #60 was fixed.** This paragraph originally read
> that `playbackSessionTeardown.ts` issued the request "as a `keepalive` `fetch`
> from an unload path". **Both halves were wrong.** The primary issuer is the
> explicit stop path (`releasePlaybackSessionOnStop()`), not the unload path, and
> it was **not** `keepalive` — only the `pagehide`/`visibilitychange` flush asked
> for that transport. An ordinary `fetch` belongs to its document's fetch group
> and is aborted when the document is destroyed, and because `release()` marks
> the record released *before* dispatching, the keepalive `pagehide` backstop
> that fired moments later found nothing owed and issued nothing. That pair of
> facts is what produced a session the server never received a single request
> about. The server-side reading above is unaffected. Fixed by
> `tesserafin-web` PR #61, `855e2e3c25de…`; see §8.

Observed rate: **1 failure in 8** runs of that spec against this web artifact
(rehearsal 0 FAIL, rehearsal 1 PASS, gate attempt 1 rounds 1–3 PASS, gate attempt 2
rounds 1–3 PASS).

**Gate attempt 1 — FAILED, 3 rounds out of 3.** Two tests in
`playback-v2-server-contract.spec.ts` failed identically in every round:

```
Error: fixture "Remux Probe (2022)" not found on the server
Error: no external subtitle stream on the fixture; streams=[["Video",0],["Audio",1]]
```

This was a **defect in the validator, not in the product**: its fixture seeding
created only the two movies and omitted the Matroska remux probe and the SubRip
sidecar that `ci/serve-e2e.sh` also creates, and it created only the `Movies`
library and not `Codec Probes`. Fixed by mirroring `ci/serve-e2e.sh`'s full
four-fixture, two-library contract, after which all 16 specs pass. The failure is
recorded because a missing fixture surfaces as a product-shaped error, and a gate
that quietly seeds a subset is a gate that quietly tests a subset.

### Other gates

| Command | Result |
|---|---|
| `ci/run.sh` (server, `bin/`/`obj/` purged first) | **PASS** — build succeeded, full test suite green (307 s wall time) |
| `npm run validate:full` (web, at `fa47bab7f0`) | **PASS**, working tree clean — includes `verify:bundle-budget` (373.4 KiB against a 450.0 KiB budget), `verify:tesserafin-sdk-fresh`, `verify:tokens-fresh`, `verify:no-new-reefin` (1362 files scanned) |
| `~/.local/bin/shellcheck -x ci/verify-release-pair.sh ci/run.sh` | clean, exit 0 |

---

## 7. Known limits

1. **Hosted CI was NOT restored and did not run.** Every result here was produced
   on one developer machine, by hand. GitHub Actions remain parked for this
   repository (#62); restoring enforced, off-laptop CI is #94 / [C1] and the
   hosted contract gate is #97 / [C4]. `ci/verify-release-pair.sh` is a local
   gate. It is not a required status check, it cannot block a merge, and it must
   not be described as CI.

2. ~~**The teardown `DELETE` is racy**~~ — **RESOLVED.**
   `tesserafin-project/tesserafin-web#60` is closed by `tesserafin-web` PR #61,
   merged as `855e2e3c25de42cec292192149947309678f5250`. Every teardown now uses
   the unload-survivable transport and a dispatch that never happened no longer
   consumes the session's one release. 25 consecutive targeted teardown runs and
   three container-fresh lifecycle rounds inside this validator are green; see
   §8. This limit is kept in place, struck through, because the `Refs #93` on the
   PR that introduced this document was justified by it.

3. **`linux/amd64` only.** The arm64 child image was built, published and had its
   OCI labels verified. It was not booted and no browser ran against it. Same
   coverage boundary as A1–A6.

4. **Bounded scope.** A7 is not B1. Library and search parity, error-message UX,
   responsive and accessibility validation, bundle-budget redesign and the removal
   of remaining Jellyfin dependencies are Section B and were deliberately not
   attempted. Only the flows needed to prove the reconciled contract and the
   carried playback lifecycle were run.

5. **The contract pin is 13 commits behind, legitimately.** The web SDK's
   provenance names `8c358f93`, not `99783b2a74`. That is not drift: no commit
   between them touches `openapi/`, and the validator asserts the contract bytes
   at both commits are identical. Re-pinning would have changed provenance
   metadata without changing a single byte of contract.

6. **`w2-srv-plan-preserve-v2` still exists on the remote.** It has no dependants
   (see §2) and was left in place rather than deleted.

7. **`docs/local-ci.md` still names the pre-rename repository slugs** in both
   repositories. That is #101 identity scope, not an A7 dependency, and was not
   rewritten here.

8. **`--skip-e2e` and `--skip-openapi-regen` exist.** They are iteration aids.
   Both mark the run DEGRADED and force a non-zero exit, so neither can produce a
   green run — but a reader of a transcript should still check which flags were
   passed.

---

## 8. The reliable pair — how #93 closes

The pair in §1 supersedes the first one. Nothing about the server changed: the
only difference between the two server source commits is the bundled web pin.

| | superseded | closing pair |
|---|---|---|
| server source | `99783b2a743ee62617c77ffb12046f788c229e1c` | `a39f28c6406ab4e444ad5bd1837f447faeafdb96` |
| server manifest | `sha256:e7551ac881bab01c10f290103ca3905e2779fbc1da5110aa11a26b0364a9f1f8` | `sha256:032afb13a54b50823a2acbf30465f4e1f63efa26dacd041cbf50ba11df44994a` |
| `linux/amd64` | `sha256:0d31dadff3bbef76385157292f4a0aa3e44786ef448a599decba1fa5a529ef04` | `sha256:6ec5eb211b6cb24ef4a6853deaec0f5f69b1d8aa1e6c25c3ef81c355e4a177d9` |
| `linux/arm64` | `sha256:7b73b5225d2e77d88c0e9e9394d296ddc1e6cc14ad1d1898ff0b6305226b61f9` | `sha256:21e2bb908138e4d4a1a794413084716ed14ad6906baabbee84aa4f10a0b9fed9` |
| web source | `fa47bab7f09d635f0b79b0814ddff2a1a1108400` | `855e2e3c25de42cec292192149947309678f5250` |
| web assets | `sha256:357afd28932481f6c02a521c6482dcace58b5102190e896f79c6f515fd440a5b` | `sha256:8ff18f389ab1d0f8fbc3a311a080c377fda9bad766167becbd4855a75207b5dd` |

### What the client actually did wrong

`PlaybackSessionTracker.release()` dispatched the teardown over the ordinary
`fetch` transport unless the caller asked for `keepalive`, and only the
`pagehide`/`visibilitychange` flush asked. So the **primary** route — the stop
path — used a request that belongs to its document's fetch group and is cancelled
when that document is destroyed. And because `release()` marks the record released
*before* issuing (correctly: that is what makes a second trigger a no-op rather
than a duplicate), the keepalive backstop that fired moments later found nothing
owed. **Both** had to be true to produce the observed symptom: the server received
no request at all on the session path and held the client-owned session to its
TTL.

`tesserafin-web` PR #61 makes every teardown use the unload-survivable transport,
whichever trigger issues it, and makes `release()` report whether the request was
actually handed to the transport. A dispatch that never happened — an absent or
synchronously throwing `fetch` — no longer consumes the session's one release, so
the backstop stays armed; a request that *left* and then failed is deliberately
not re-armed, because re-issuing later is the stale-teardown hazard the design
forbids and the TTL sweep is the last-resort recovery reserved for it.

No server change was required, and none was made. The TTL is unchanged,
`TranscodingJobEnded` still does not reap a client-owned session, and the
client-owned-session rule from #72/#74 is untouched.

### Evidence

**25 consecutive targeted teardown/navigation runs — PASS=25, FAIL=0.**
The oracle's first test only: `POST` → DirectPlay → real product error → `PUT`
re-plan to Transcode → `ffprobe`-proven transcoded bytes → liveness probe from
outside the browser → user stop with immediate navigation → server-logged
`deleted` → genuine `404`. One real server (`ci/serve-e2e.sh`, newly synthesised
fixtures) with a fresh playback session and a fresh browser context per run —
**not** a fresh container per run; the container-fresh rounds are the three below.
The run is 6–8 s and each one added exactly one `deleted` line to the server log.
Across all 25: `deleted` 25, `already gone` 0, `PlanNotExecutable` 0,
`served from legacy` 0.

That stress ran against a `dist/` built from the web source that became
`855e2e3c25de…`, served by `ci/serve-e2e.sh` — source-identical to the merge
commit but a *different artifact* from the published web-assets image. The
published pair is proven by the validator rounds below, not by the stress.

**`ci/verify-release-pair.sh`, full run, no skip flags — RESULT: PASS.**

```
ci/verify-release-pair.sh \
    --server-image ghcr.io/tesserafin-project/tesserafin@sha256:032afb13a54b… \
    --server-source a39f28c6406ab4e444ad5bd1837f447faeafdb96 \
    --web-repo <tesserafin-web checkout> \
    --web-source 855e2e3c25de42cec292192149947309678f5250 \
    --lifecycle-runs 3
```

| Layer | Result |
|---|---|
| ARGUMENTS | PASS — both checkouts *are* the named commits |
| IMAGE PROVENANCE | PASS — `docker/version-verify.sh --require-digest --expect-commit` |
| BUNDLED WEB PROVENANCE | PASS — label, in-image `/opt/tesserafin-web.revision.json`, `Dockerfile` `WEB_VCS_REF` and both arch children all name `855e2e3c25de…`; assets pinned by digest; 2 runnable architectures |
| OPENAPI | PASS — regenerated `sha256 0700633b3c12…` == committed |
| GENERATED SDK | PASS — zero regeneration drift; the contract at the SDK's provenance commit `8c358f930c2b…` is byte-identical to the release contract and an ancestor of it |
| BROWSER E2E | PASS — `docker/browser-onboarding.sh` against the release image on pristine volumes |
| LIFECYCLE CONTRACT | PASS — 3 consecutive rounds |

| Round | Container | Result |
|---|---|---|
| 1 | new container, new volumes, newly synthesised fixtures | PASS — 16 passed (35.7 s) |
| 2 | new container, new volumes, newly synthesised fixtures | PASS — 16 passed (35.6 s) |
| 3 | new container, new volumes, newly synthesised fixtures | PASS — 16 passed (35.7 s) |

**Other gates.**

| Command | Result |
|---|---|
| `ci/run.sh` (server, `bin/`/`obj/` purged first) | PASS — build succeeded, full test suite green (263 s wall time) |
| `npm run validate:full` (web, at `855e2e3c25de…`) | PASS — 76 files / 896 tests, bundle 373.5 KiB of 450.0 KiB, tree clean |
| web SDK drift with a real server checkout | none |
| `docker/verify-assets-image.sh` on the new assets image | ASSETS-AUDIT: all gates passed |

### Runs that failed, kept on the record

**Teardown stress attempt 1 — 24/25, run 1 red.** Not a lost `DELETE`: that
session was deleted and 404 afterwards. It failed a *new* assertion that was too
strong — it required the teardown to be dispatched by the trigger named `stopped`
in every run, and run 1 dispatched from `pagehide`. That is a property of the
harness, not of the contract: `stopPlayback()` presses Escape and navigates
immediately, and in that flow the navigation is what ends playback
(`beforeunload` → `onAppClose()` → the stop path, then `pagehide` → the
backstop). When the manager no longer considers itself playing at `beforeunload`,
the backstop is legitimately the only route left. The assertion was corrected to
the property the contract does guarantee — exactly one dispatch, from a
recognised trigger — which still fails loudly at zero and at two. The
deterministic stop-before-navigation ordering is proven by unit tests, which is
where it can be made deterministic.

**A stress-rig bug, before any product run.** The script defaulted a `grep -c`
with `|| echo 0`, which emits two lines when nothing matches and aborted the loop
on an arithmetic error. No product conclusion was drawn from it.

### Still true

Hosted GitHub Actions were **not** executed for any of this. Limits 1 and 3–8 of
§7 stand unchanged; only limit 2 is resolved.
