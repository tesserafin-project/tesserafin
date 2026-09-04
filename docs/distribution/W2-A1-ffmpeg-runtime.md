# W2-A1 — consuming the accepted Windows FFmpeg runtime by digest

Tracker: [#256](https://github.com/tesserafin-project/tesserafin/issues/256).
Base: `3d0c80047ce418ec4255ff3d788fe8d48a3da3e3` (W2-A0 accepted on master).
Umbrella: #234.

W2-A1 proves one thing, and deliberately only one thing: on a clean
`windows-latest` runner, W2's production FFmpeg acquisition is exactly

```
ci/windows/runtime-retention/consume.ps1
```

driven by the committed

```
ci/windows/runtime-retention/accepted-runtime.json
```

and nothing else. Both of those files are W1's. This slice authored neither of
them, forked neither of them, and changed neither of them; a raw-byte pin
(control F16) is what makes that a measured statement rather than an assurance.

## What this slice added

| Path | What it is |
| --- | --- |
| `.github/workflows/w2-windows-ffmpeg-consume.yml` | the hosted proof, `pull_request` only, one `windows-latest` job |
| `ci/windows/w2/ffmpeg-consume-controls.py` | 20 hostile controls over the frozen consumer and the new workflow |
| `docs/distribution/W2-A1-ffmpeg-runtime.md` | this document |

There is **no W2 wrapper script**, and that absence is the point. A wrapper is
where a `-Reference`, a `-Tag` or a `-RunId` would eventually be added, and the
security property of the frozen consumer is precisely that it offers no such
parameter: the identity of what W2 builds against travels with the commit W2 is
building. The workflow therefore calls the frozen consumer directly, with the
four arguments it actually has, and control F10 asserts that the argument set
stays exactly

```
-AcceptedManifest  -WorkDir  -OutDir  -OrasPath
```

with `-AcceptedManifest` naming the committed acceptance manifest literally.

## The identity

| Fact | Value |
| --- | --- |
| accepted runtime reference | `ghcr.io/tesserafin-project/windows-ffmpeg-runtime@sha256:99e45f154a5d72aba4185eb19b6671aa1a11c30be837deac9dd26f473593c0b9` |
| inner runtime archive | `delivered/runtime/tesserafin-ffmpeg-7.1.4-tesserafin.1-win-x64.zip` |
| inner runtime archive SHA-256 | `f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e` |
| descriptive tag (never a trust boundary) | `accepted-83e23b957940` |

The tag appears nowhere in the production path. Control F11 asserts that the
workflow and the consumer name neither it nor any other tag of the accepted
package, and controls F01–F05 drive the real consumer with a disposable
acceptance manifest carrying, in turn, that tag, a short digest, the accepted
digest in uppercase, a tag and the digest together, and the accepted digest
under another package name. Every one is refused with the consumer's own named
reason, before the registry is consulted at all.

## The two consumptions

The job consumes the accepted runtime **twice**, into two directories that have
never held anything, and then requires:

* each inner runtime archive to hash to
  `f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e`;
* the two delivered directories to carry the same file names, each pair
  byte-identical by digest;
* the two inner runtime archives to compare equal **byte for byte**, not merely
  by digest.

The expected value is stated in the workflow's `env:` and is required to equal
the committed `accepted-runtime.json` field before either comparison runs, so a
drift in either place is a red rather than a silently relaxed test. Control F12
holds the ruling's constant, the acceptance manifest and the workflow env
against each other; control F19 asserts the workflow really does consume twice,
into two distinct `-WorkDir`/`-OutDir` pairs.

The two measured hashes are printed by the job itself, one line per
consumption, and the consumer's own evidence document is published to the run
summary. The hosted run for this slice is cited in the pull request rather than
here, because this document is committed before that run exists.

For orientation, the same frozen consumer measured on Linux against the same
committed manifest during development:

```
verified 61 retained paths
runtime  f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e
source   d753268c14d8e312bdd8ccd5ce8af90d495d185e807b77621e229eb8f71cc76d
stream   5158221a246c7e7d0d843d649571625ad0277152a093361419414195a8afee8e
```

That is orientation, not the proof. Only the hosted `windows-latest` run is
W2-A1's evidence.

## The pull is anonymous

The accepted runtime package answers an unauthenticated pull by digest. The job
therefore carries **no token, no PAT and no repository secret**, and there is no
`packages:` grant anywhere in the workflow at either scope — the workflow-level
grant is `contents: read` and the job repeats it. To make anonymity a checked
property rather than an accident, both consume steps point `DOCKER_CONFIG` at an
empty directory, so no credential the runner image happens to carry can be used.
Control F17 asserts all of that, and refuses to pass if the empty credential
store is removed.

## The corresponding source

The frozen consumer verifies both halves of the GPL-3.0-or-later obligation: the
runtime archive and the corresponding-source archive, the latter by its
container digest **and** by the digest of its decompressed stream. That check is
the consumer's, and this slice does not reimplement it. It requires `zstd` on
the runner, so the workflow asserts `zstd` is present — with a Git-for-Windows
fallback and a named hard stop — before consuming, rather than letting the
consumer's own refusal read as a digest failure.

## Retained limitations

1. **`zstd`'s identity is the runner image's, not a pin.** `windows-latest`
   documents zstd 1.5.7. It is a decompressor whose output is verified against a
   committed digest, so a substituted zstd fails closed rather than silently,
   but it is not itself a pinned input the way ORAS is.
2. **`tar` is likewise the runner image's.** The frozen consumer reads the layer
   with `tar --list` before extracting anything and verifies every extracted
   path and digest afterwards, so the same argument applies, and no more.
3. **Dual-runner reproducibility is not claimed.** The two consumptions run on
   one runner allocation. What they prove is that the acquisition is a function
   of the committed digest and not of the machine's state between two clean
   directories; W2's §8.1 two-runner requirement belongs to the ZIP build, not
   here.
4. **The four network-touching controls (F06–F09) need `ghcr.io`.** They fetch
   the accepted 1851-byte manifest and no blob. On a host that cannot reach the
   registry they report RED, not a smaller green suite.
5. **W1's own negative controls were not touched.** `negative-controls.py`
   already asserts the absence of a caller-supplied reference on the consumer.
   F10 is W2's own, independent assertion of the same property plus the new
   workflow's argument set; it does not replace or weaken W1's.

## What this slice does NOT do

It does not:

* build, assemble, name or emit a **ZIP** of any kind;
* build the server, publish anything, or write any package;
* **relocate** a tree, **start** a server, or check readiness;
* install or register a **service**;
* rename or migrate the `.reefin-*` markers;
* consume the Web payload again (that is W2-A0's accepted slice);
* claim W2 is accepted, or that any W2 acceptance criterion beyond
  digest-pinned FFmpeg acquisition has been met.

W2's remaining requirements — deterministic archive metadata, one canonical
top-level directory, no shipped state, hostile-path relocation, cold first
start, marker migration — are untouched by this slice and remain open on #256.
