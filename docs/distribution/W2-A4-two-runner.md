# W2-A4 — Two-runner bit-identical `win-x64` server ZIP

Tracker: [#256](https://github.com/tesserafin-project/tesserafin/issues/256).
Umbrella: #234. Base master:
`8718096aa5f56d33a5b4fad91935ae89037a6e7b`, which is W2-A3 as accepted.

This slice proves one thing, on two `windows-latest` runners:

> two clean assemblies of `tesserafin-server_<version>_win-x64.zip` from the
> same commit, on two **separate runner allocations that share nothing**, hash
> to the same 64 hex digits.

It is one slice of W2. **W2 is not accepted by this document.**

---

## 1. What §8.1 means here, and why W2-A2 did not already prove it

[W0 §8](W0-windows-server.md) clause 1 is the requirement, and it excludes the
shape W2-A2 delivered in the same sentence:

> **Two clean `win-x64` builds on two separate native Windows runners.** Not
> two builds on one runner; not one build compared to a cache.

W2-A2 packed the archive **twice on one runner**, into two directories that had
never held anything, and said so in its own words: its workflow's header records
that "§8.1 two-runner identity is NOT claimed". That proof is real and it is
not repeated here — it establishes that the assembler is deterministic against
two clean trees. What it cannot establish is that the archive is deterministic
against a *machine*: one allocation's installed SDK patch level, its temporary
directory layout, its locale, its disk ordering and its `%TEMP%` path are all
constant across two packs on that same allocation, and every one of them is a
plausible source of a byte that differs between two machines.

So W2-A4 changes exactly one variable — the allocation — and holds everything
else fixed:

| | W2-A2 | W2-A4 |
| --- | --- | --- |
| assemblies | two | two |
| runner allocations | **one** | **two** |
| assembler | frozen `assemble-server-zip.ps1` | the same frozen file, unedited |
| `SOURCE_DATE_EPOCH` | derived once, given to both packs | derived independently on each allocation, from the same named commit |
| shared state between the two | the runner itself | none |
| what crosses a job boundary | nothing | 65 bytes of hex |

Clause 2 — "**Nothing shared between them.** No compiled objects, no dependency
prefix, no staged runtime, no incremental mode" — is what the two jobs are
built to satisfy, and it is asserted rather than assumed:

* the two assemble jobs declare **no `needs:`** on each other, so neither is
  scheduled after the other and neither can observe the other's outputs;
* there is **no `actions/cache`**, and no `cache:` input on a setup action,
  which is the spelling a rule naming only the action would miss;
* each job requires its work tree and its output tree to be **absent** before
  the assembler is handed either path, and fails loudly if one exists;
* each job pulls the accepted Web payload and the accepted FFmpeg runtime for
  itself, by digest, through the frozen consumers.

Clause 4 — "**Bit-for-bit identity of every unsigned delivered byte**" — is the
verdict. The comparison is over SHA-256 of the whole archive, which is a
statement about every byte of it; the archive is not reduced to a delivered-path
list or to functional equivalence at any point.

## 2. How `SOURCE_DATE_EPOCH` is derived

Both allocations derive it the same way, from the same named commit, and never
from anything either machine observes locally:

```
$head  = '${{ github.event.pull_request.head.sha }}'
$epoch = (& git log -1 --format=%ct $head).Trim()
```

`git log -1 --format=%ct` is the committer time of that commit — the definition
`docker/version-contract.sh` already uses for every other Tesserafin artifact.
It is never the clock, never a tag, never a run identifier and never the
workflow's start time. Each job additionally requires `git rev-parse HEAD` to
equal that same head SHA, so an allocation that somehow checked out a different
commit fails there rather than producing an archive that differs for a reason
nobody would look for.

That derivation is what makes the two values equal **by construction** across
two machines that share nothing: the epoch is a property of the commit, and both
sides are told which commit. The controls refuse a workflow whose assemble jobs
name `UtcNow`, `Get-Date`, `date +%s`, `github.run_id`, `github.run_number` or
`github.ref_name` at all.

## 3. Why only a hash file crosses a job boundary

[W0 §8](W0-windows-server.md) clause 7 is unconditional:

> **No expiring Actions artifact is ever a production input.** The W0 workflow
> uses one for the Web payload handover; that is permitted **only** because W0
> is probe evidence, and W2 replaces it with a daemonless digest-pinned OCI pull
> on the Windows host.

The archive therefore stays on the runner that made it. It is produced, hashed
and abandoned; nothing downloads it, nothing extracts it and nothing feeds it to
another job. Two small text files leave an allocation:

* `sha256.txt` — **65** bytes: sixty-four lowercase hex digits and one LF;
* `members.txt` — one line per staged file, `<sha256>` then two spaces then the
  file's posix path relative to the stage root, sorted bytewise, LF only, no
  BOM.

That file is a statement *about* a production output, not an input to producing
one — no job's result depends on its contents being trustworthy in the way §8.7
is written to protect. Being explicit about the boundary anyway: a corrupted or
substituted hash file can only ever make the compare **fail**. It cannot cause a
different archive to be built, delivered or accepted, because no archive is
built from it.

Both evidence files' shapes are enforced on both ends. The producing job writes
them with `WriteAllBytes` rather than a PowerShell text writer — which would give
them CRLF and a BOM — and fails if `sha256.txt` is not exactly 65 bytes or if
`members.txt` is empty, carries a CR or does not end in LF. The consuming job
runs `two-runner-controls.py --compare … --members …`, which reads both as
**bytes** and refuses a hash that is not 64 lowercase hex digits followed by one
LF, and a member list that is empty, BOM-prefixed, CR-bearing, unterminated,
mis-separated, uppercase, duplicated or out of order.

### 3.1 Why the member list exists (W2-A4-R1-DIAG)

Run `33967489630` measured §8.1 and **it did not hold**: the same commit, the
same `SOURCE_DATE_EPOCH` (`1788612983`) and the same `185755892` bytes produced
two different SHA-256 values. A whole-archive hash can only report *that* two
allocations disagree. It cannot report *about what*, and a byte count is not a
diagnosis.

So each allocation also walks the stage the frozen assembler leaves behind and
publishes a digest for every file in it. The relative paths are the assembler's
own entry names — `Invoke-Pack` derives both from the same stage root — so a
line in `members.txt` **is** an archive member, and "the differing members" is
meant literally. The walk reads the staged tree, never the archive: opening the
ZIP would measure the pack, and what has to be separated is *the staged files
differ* from *the container differs*. When every member agrees and the two
hashes still do not, the compare says so in as many words, because that is a
different defect with a different fix.

R1-DIAG was a **diagnosis** pass: it changed no `csproj`, set no
`Deterministic`, `ContinuousIntegrationBuild` or `PathMap`, edited neither the
assembler nor any C# file, and claimed nothing about the next run. It ran, and
§3.2 is what it found and what R2 then authorised.

### 3.2 What the member list found, and what was done about it (W2-A4-R2)

Run `33968944456` answered R1-DIAG's question. Of **2868** staged members,
exactly **one** differed:

```
tesserafin-server_1.0.0_win-x64/tesserafin.staticwebassets.endpoints.json
  A  2c74b2bd814150107dac393a346dac3aca45d6b780a735ea2d9a824fcfc7062e
  B  fe37db3934d1fcc3c90564a98c1b40e155aa515cb55751262233b82eed25d6af
```

No member was present on only one side, the 2867 others agreed, and the two
archives were `185755947` and `185755948` bytes — one byte apart. `Invoke-Pack`
is therefore not implicated: a packer defect does not spare 2867 members.

**The cause, measured rather than inferred.** The manifest's `Last-Modified`
response headers are RFC 1123 renderings of each static asset's publish-time
mtime, so they carry the clock of the machine that published. Reproduced
locally by publishing the same commit twice: the two manifests were the **same
length** (27887 bytes), carried the **same 40 endpoints in the same order**, and
differed in **`Last-Modified` alone**, on 32 of the 40. Everything else — routes,
selectors, `ETag`s, `integrity` hashes, endpoint properties — was byte-identical.

Two consequences follow, and both matter:

* RFC 1123 is fixed width, which is why two files of *identical length* pack
  into archives *one byte* apart. The one-byte delta is a compression artefact,
  not a one-byte content change.
* Entry **ordering was stable**. A canonicaliser that only sorted would have
  produced two files that still differed, and the next hosted run would have
  failed the same way with a slice spent.

**What was done.** W2-A4-R2 authorised one additional path,
`ci/windows/w2/assemble-server-zip.ps1`, and one change inside it: a
post-publish, pre-pack step. The ruling offered two implementations.

**Option 1, canonicalise in place, was taken.** After `dotnet publish` and
`Assert-SelfContained`, and before anything is staged or packed,
`Invoke-CanonicaliseEndpoints` rewrites every `Last-Modified` value to
`SOURCE_DATE_EPOCH` rendered as RFC 1123 UTC, re-serialises the document with
every object's members in ordinal order, and writes it as LF-terminated UTF-8
without a BOM. Arrays are **not** reordered: `Endpoints`, `Selectors` and
`ResponseHeaders` are sequences a reader may take in order, their order was
measured to be stable already, and sorting them would change what the file says
in order to make it easier to compare.

Rewriting the header rather than deleting it keeps the value a function of the
**commit**, never of the clock — the same clamp §6 already applies to every
archive entry's mtime — and leaves the manifest well-formed rather than missing
a header its schema declares. A missing manifest is a **refusal**, not a skip:
a publish that stopped emitting the file is a publish whose shape changed.

**Option 2, omitting the file, was available and was not taken.** The proof it
required does exist: `tesserafin.staticwebassets.endpoints.json` is read by
`MapStaticAssets`, and the server never calls it —
`Tesserafin.Server/Startup.cs:238` and `:255` serve static content with
`UseStaticFiles`, which reads the file system through the web root provider and
opens no manifest, and nothing on W2-A3's start path names the file either. It
was not taken because canonicalising stays correct **without** relying on that
absence, and keeps the staged tree the thing `dotnet publish` produced.

Verified locally against the two divergent publishes: both converge on one
digest, a second pass over the output is a no-op, a different epoch produces
different bytes — so the clamp is load-bearing and not inert — and every value
except `Last-Modified` survives unchanged, with `Version` still an integer and
the `ETag` quoting intact.

**Nothing else changed.** No `Deterministic`, no `PathMap`, no
`ContinuousIntegrationBuild`, no `csproj`, no `Directory.Build.props`, no C#.

### 3.3 The pins the assembler edit moved (W2-A4-R2-S15)

Changing an accepted file moves every raw-byte pin that names it, and W2-A2 and
W2-A3 both pin the assembler on purpose. That is the mechanism working, not a
side effect to route around: it is how "do not edit an accepted file" is stated
in something other than prose.

Three pins moved, in a chain, each authorised:

* `ci/windows/w2/start-controls.py` — W2-A3's accepted suite — pins the
  assembler in its own `FROZEN_PINS`, asserted by `S15`. W2-A4-R2 authorised the
  assembler and not this file, so `S15` went `RED`, and because A3's workflow
  runs its controls **before** the start proof and throws on a non-zero exit,
  that `RED` would have failed A3's hosted job at its controls step and it would
  never have started the server at all. **W2-A4-R2-S15** then authorised this
  file for that one pin value and nothing else; it is now
  `b4fbb81538e5fdefb26928373bee61969c141733b3fae91159e0198092f94f33`. No other
  pin, control, roster or oracle in it changed.
* `T15` in this suite pins the assembler and was moved to the same value.
* `T15` also pins `start-controls.py` itself, so moving that file's bytes moved
  this pin too, to
  `ae1bfb060dcb224b5da2a5724dbaa44b28e8b0b2b775fb9624436ca8058cf5b0`.

Every other pin in all three suites is unchanged, and each suite asserts its own
set: A2 pins four files, A3 five, A4 ten.

## 4. The compare cannot be skipped, and cannot conclude without both sides

A two-job proof has one characteristic way of going quietly wrong: the compare
job declares `needs:` on both assemblies, an assembly fails, the compare is
**skipped**, and a skipped job renders as neither red nor green. A reader — or a
branch-protection rule — can mistake "never ran" for "agreed".

So the compare job declares `if: ${{ always() }}` and runs whatever happened
upstream, and its **first** step fails the job unless both allocations reported
`success`.

That step used to carry the failure condition as its own `if:`, which meant it
**skipped** on every run where both allocations succeeded — that is, on every
run that has a pair to compare. A guard that is invisible on the healthy path
cannot be read as having held, so it now runs on every path and decides in the
shell:

```
if: ${{ always() }}
run: |
  a='${{ needs.assemble-a.result }}'
  b='${{ needs.assemble-b.result }}'
  echo "assemble-a: ${a}" ; echo "assemble-b: ${b}"
  if [ "${a}" != 'success' ] || [ "${b}" != 'success' ]; then … exit 1 ; fi
```

Either way the missing-evidence refusal does not depend on this step: `--compare`
is given four paths and fails on any one of them being absent or malformed.

The comparison itself is a function in `ci/windows/w2/two-runner-controls.py`
rather than a shell snippet in the YAML, for one reason: a snippet can only be
*audited*, and an audit cannot distinguish "refuses a missing file" from "was
never given one". As a function it is driven as a subprocess by the controls
against planted evidence files — missing on either side and on both, empty,
truncated, over-long, unterminated, CRLF, uppercase, non-hex, BOM-prefixed,
split over two lines, and simply different. Every one must exit non-zero and say
`NO IDENTITY`; the single well-formed agreeing pair must exit zero and print the
hash.

## 5. The controls

`ci/windows/w2/two-runner-controls.py` runs on `ubuntu-latest`, needs nothing
and assembles nothing, so a defective suite is visible in seconds rather than
behind two hours of Windows time. It reports twenty-four rostered controls plus
`ROSTER` and `RESTORE`; a control that is deleted or renamed makes the run fail
rather than making the summary line shorter.

| control | what it refuses |
| --- | --- |
| `T01` | a workflow that is not the pinned bytes |
| `T02` | the W2-A0-V1 shape: a `push` trigger, `write-all` at either scope, an extra job, and reads that exist only in a comment |
| `T03` | fewer or more than two assemble jobs, one not on `windows-latest`, a second assembler call, a shared work or output tree, and a `needs:` edge between the two |
| `T04` | `actions/cache`, a `cache:` input on a setup action, `restore-keys:` |
| `T05` | an upload of the archive or of a glob, an upload without `if-no-files-found: error`, a download inside an assemble job, a release asset |
| `T06` | a compare without `always()`, without a `needs.<job>.result` guard that exits non-zero, or that does not run the audited comparison |
| `T07` | a compare that accepts a missing hash file |
| `T08` | a compare that accepts a malformed one |
| `T09` | a compare that accepts two different hashes, or rejects one agreeing pair |
| `T10` | an epoch that is not the head commit's committer time |
| `T11` | an assembly into a tree that was not asserted absent, a `-StageRoot`, a caller-chosen identity |
| `T12` | a start, a relocation, a service registration, a `.reefin` edit, a container engine |
| `T13` | a write grant at any scope, `pull_request_target`, `workflow_dispatch`, `workflow_call`, `schedule`, `push`, an unauthorised job |
| `T14` | a disagreement between the ruling, the frozen assembler, the committed acceptance manifest and this workflow |
| `T15` | any edit to a previously accepted W2 file, including the frozen assembler and `ffmpeg-consume-controls.py` (F18) |
| `T16` | a new `.ps1` under `ci/windows/w2/` |
| `T17` | a `paths:` filter that names files instead of watching the tree |
| `T18` | an unpinned action, a persisted credential |
| `T19` | a controls job that assembles, or that hides behind a `needs:` |
| `T20` | this document, if it drops a non-goal or claims acceptance |
| `T21` | an allocation that publishes no member list, a walk that reads the archive instead of the stage, a non-ordinal sort, a text writer, or a compare handed no `--members` |
| `T22` | a compare that accepts a missing, empty, BOM-prefixed, CRLF, unterminated, uppercase, short, mis-separated, path-less, backslashed, duplicated or unsorted member list |
| `T23` | a compare that fails to name a changed member, a member present on only one side, or a disagreement whose members all agree |
| `T24` | a canonicalisation that is absent, runs before the publish or after the pack, is not given `SOURCE_DATE_EPOCH`, or deletes the manifest instead — and a compare that would no longer name `tesserafin.staticwebassets.endpoints.json` if it came back as a differing member |

Every audit rule is proved load-bearing by a **mutation of a structural
baseline** that the audit must name: a planted cache, a `needs:` edge between the
assemble jobs, an upload of the `.zip`, a compare with its `always()` and its
result guard removed, an epoch taken from `UtcNow`, a compare handed only the
two hashes, an allocation that walks no stage. A rule whose mutation no
longer applies reports `INERT` rather than passing — a refusal that cannot be
shown to bite is a comment.

`T24` was built the same way and the mutations found two holes in it before the
branch was committed: a canonicalisation handed `UtcNow` passed, because
`-Epoch $SourceDateEpoch` also appears on both `Invoke-Pack` calls and a
whole-file search was satisfied by the packer's; and a deletion written through
the `$ENDPOINTS_MANIFEST` variable passed, because the rule knew only the
literal filename. Both are closed, and all six mutations — the call removed,
the epoch taken from the clock, the step moved after the pack, the manifest
deleted by variable, deleted by literal, and never named at all — now report
`RED`.

## 6. The measured result

The two hosted hashes, the two archive sizes, the two runner allocations and —
since R1-DIAG — the paths of every differing member are cited in the pull
request. They are not in this file: the document is committed **before** the run
that produces them, and a digest written down before it is measured would be a
prediction rather than evidence. That cuts both ways: this file does not predict
that the members will agree, or that the next run will be identical.

## 7. Non-goals

This slice is deliberately narrow. It:

* **does not start** `tesserafin.exe`. Nothing here launches the server, waits
  for a port or requests `/`;
* **does not relocate** the extracted tree. Relocation is W0 §2.3's proof, it is
  W2-A3's, and it is already on master;
* adds no first-party **service script** and registers no service. W0 §6's last
  bullet stays deferred and W4 owns the SCM;
* renames no `.reefin` marker and changes no server C# file;
* does not **publish** anything: no package write, no release asset, no
  registry push, no tag;
* uses no container engine, no daemon and no CLI for one;
* adds no `.ps1`, and edits two previously accepted W2 files, each authorised
  by name: `ci/windows/w2/assemble-server-zip.ps1`, under W2-A4-R2, for the one
  post-publish canonicalisation step of §3.2 and nothing else; and
  `ci/windows/w2/start-controls.py`, under W2-A4-R2-S15, for the one `S15`
  assembler pin value of §3.3 and nothing else — no other control, roster,
  oracle or pin in it, and no weakening of `S01`–`S14`, `S16` or `S17`.
  `consume-web-payload.ps1`, `relocate-and-start.ps1`, `pkg-tree-digest.py`,
  `zip-controls.py`, `ffmpeg-consume-controls.py` (F18), the runtime-retention
  consumer and the acceptance manifest are all untouched, and `T15` still pins
  them;
* does not claim the MSI, the signing story or the acceptance matrix;
* **does not tune the build for reproducibility.** No `Deterministic`, no
  `ContinuousIntegrationBuild`, no `PathMap`, no `csproj`, no
  `Directory.Build.props` and no C# change is in this slice. §3.2 clamps one
  published file whose bytes encoded publish *time*; it does not address any
  other class of nondeterminism, and it makes no claim about what a later slice
  will need;
* **does not predict that the next run agrees.** R2 removes the one cause the
  member list actually measured. Whether a second cause exists is a question
  only the next hosted compare can answer, and this document is committed
  before it runs.

**W2 is not accepted by this slice.** Independent review is the next gate.
