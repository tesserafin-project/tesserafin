# Published forward-migration evidence (#127)

Status: hosted preflight executed and blocked, 2026-10-20. No successful migration run is claimed. Preparation PR: [#316](https://github.com/tesserafin-project/tesserafin/pull/316).

## Selected boundary and outstanding image proof

[#127](https://github.com/tesserafin-project/tesserafin/issues/127) records the canonical baseline:

- Build package: `ghcr.io/tesserafin-project/tesserafin-server`.
- Manifest: `sha256:fd1fa9e0f5a28a07e5872cc5ff13257a92d988717a33519f62c4b26c6ab36249`.
- Source: `44f5ab62b522684b4fa58ed10de80b8c6a7bb392`.

The source migration boundary exists: content-pack migration `20260807161612_AddContentPacks` is absent from the baseline and present in the source tree tagged `v1.1.0`. The [annotated tag](https://github.com/tesserafin-project/tesserafin/releases/tag/v1.1.0) resolves to `bb2234aebbf0ff2c1cbe291df48f2ffa62beea46`.

That source fact does **not** prove publication of a matching container:

- Anonymous registry tag listing on 2026-10-20 returned only `1.0.0` for `ghcr.io/tesserafin-project/server`.
- The Compose file at `v1.1.0` still names the public `1.0.0` image, manifest `sha256:bc7587bc7d99b40d77770046d49a2931ff8162b6d1df3da8d3f108b7677d7dc3`, source `6227dbef66c631081e3179a521561b32b94dac59`.
- The private build package requires authentication. Its published candidates must be checked from a runner with package-read access.
- The GitHub release contains native packages; these are not interchangeable with a container digest.

The first candidate lookup is the version contract's source tag, `tesserafin-server:sha-bb2234aebbf0ff2c1cbe291df48f2ffa62beea46`. This is a lookup to verify, not an asserted publication. If absent, identify an existing published image and its exact source/release provenance before changing the selected candidate. No historical rebuild, new release, tag movement or substitute baseline is authorized by this rehearsal.

## Reproducible execution

The path-scoped `Published forward migration` workflow runs on same-repository pull requests and is manually dispatchable once available on the default branch. It is an evidence job, not a required merge context; fork PRs do not receive private-package access.

It uses an ephemeral Ubuntu Docker runner, pinned checkout/upload actions, a package-read-only job token and temporary Docker credentials removed at the end. It publishes no image or release. The private baseline remains a historical test input.

The wrapper:

1. Verifies that the baseline and selected candidate manifests are readable.
2. Pulls Linux amd64 images and checks full OCI source revisions, platform and unambiguous registry digests.
3. Retains only non-secret identity fields in `images.json`; it does not upload full Docker inspection/configuration data.
4. Invokes the existing `docker/upgrade-roundtrip.sh` with both digest references and `--require-migration`, leaving its preservation assertions intact.
5. Retains the combined log, manifests and resolved identity report for 14 days, including preflight failures.

For a local Docker host already authorized to read the historical package:

```sh
bash ci/upgrade/published-forward-migration.sh
```

The execution environment used to prepare this PR has no Docker daemon and cannot create the required namespaces. Local syntax checks are not a migration result.

## Acceptance and limits

### First hosted result

[Run 39423877774, job 118631385687](https://github.com/tesserafin-project/tesserafin/actions/runs/39423877774/job/118631385687) checked out wrapper revision `628c2f1c968af9544e8c218e1d64ba29f1540cc4` on Ubuntu 24.04:

| Check | Result |
| --- | --- |
| Four preflight tests, eight mock scenarios | Passed; these are wrapper controls, not a migration |
| Registry authentication | Succeeded; this alone does not prove package-read access |
| Canonical baseline manifest | `manifest unknown` |
| Candidate source-tag manifest | `denied` |
| Server/container startup, migration, preservation harness | Not executed; preflight exited 1 |
| Evidence upload and registry-credential cleanup | Succeeded |

The result does not distinguish missing/retired content from package authorization restrictions. Resolve access to the exact historical baseline and establish a readable, published candidate digest with source provenance before rerunning. The `v1.1.0` source tag and native release assets do not satisfy that container prerequisite.

A successful image preflight is not a successful upgrade. #127 stays open until the original harness exits zero with non-zero pending migrations, successful application and all original preservation assertions passing: users, libraries, visible items, playback state, configuration, volume identity/ownership and version agreement.

The harness currently counts application migration-stage log entries. The content-pack change is an EF Core SQLite migration; verify that the actual historical runtime logs expose this boundary to the existing counter. If a real EF migration runs while the application-stage count remains zero, keep the gate red and add explicit EF execution evidence rather than deleting `--require-migration`.

Any source-tag lookup failure, access denial, revision mismatch, zero migration count or lost state is a concrete unmet gate. Retain its evidence; do not close #127 on a prepared workflow or substitute a locally rebuilt image for a published artifact.

This work exercises a forward upgrade on synthetic disposable data. It changes no server runtime, deployment default or existing release.
