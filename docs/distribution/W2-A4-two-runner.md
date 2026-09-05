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

This is a **diagnosis** slice. It changes no `csproj`, sets no
`Deterministic`, `ContinuousIntegrationBuild` or `PathMap`, and edits neither
the assembler nor any C# file. **Nothing here claims the next run will be
identical.**

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
behind two hours of Windows time. It reports twenty-three rostered controls plus
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

Every audit rule is proved load-bearing by a **mutation of a structural
baseline** that the audit must name: a planted cache, a `needs:` edge between the
assemble jobs, an upload of the `.zip`, a compare with its `always()` and its
result guard removed, an epoch taken from `UtcNow`, a compare handed only the
two hashes, an allocation that walks no stage. A rule whose mutation no
longer applies reports `INERT` rather than passing — a refusal that cannot be
shown to bite is a comment.

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
* edits no previously accepted W2 file, and adds no `.ps1`;
* does not claim the MSI, the signing story or the acceptance matrix;
* **does not make the two archives identical.** It names what differs. No
  `Deterministic`, no `ContinuousIntegrationBuild`, no `PathMap`, no `csproj`,
  no `Directory.Build.props` and no C# change is in this slice, and it makes no
  claim about what a later one will need.

**W2 is not accepted by this slice.** Independent review is the next gate.
