# W2-A2 — a deterministic `win-x64` server ZIP

Tracker: [#256](https://github.com/tesserafin-project/tesserafin/issues/256) ·
umbrella [#234](https://github.com/tesserafin-project/tesserafin/issues/234) ·
base `d04689277c61e59e9b15815cb6de92ee67ef458e`

W2-A2 adds a packer and a proof. It does not start a server, does not relocate
one, and does not claim §8.1's two-runner identity. What it establishes is
narrow and mechanical:

> two clean assemblies of `tesserafin-server_<version>_win-x64.zip` from the
> same commit, on one `windows-latest` runner, into two directories that have
> never held anything, produce the same bytes — and no archive is produced at
> all when any input is not the accepted one.

Everything in the archive is either acquired through a consumer this slice does
not author, or derived from the commit being built.

| what | how it is acquired | frozen by |
| --- | --- | --- |
| Web payload | `ci/windows/w2/consume-web-payload.ps1` | W2-A0 |
| FFmpeg runtime | `ci/windows/runtime-retention/consume.ps1` driven by `ci/windows/runtime-retention/accepted-runtime.json` | W1 / W2-A1 |
| server | `dotnet publish`, self-contained, `win-x64` | this commit |

There is deliberately **no W2 wrapper** around either consumer. A wrapper is
where a `-Reference`, a `-Tag` or a `-RunId` eventually gets added, and the whole
security property of the frozen consumers is that the identity of what W2 builds
against travels with the commit. `Z09` asserts the assembler grows no such
parameter and that the workflow passes none.

---

## 1. The frozen relative layout

This is the layout W3's MSI reuses. §6 requires "the same set the MSI installs,
at the same relative paths", and §9.1 places the Web payload, the FFmpeg runtime
and the licences under the application directory. Measured from a real assembly:

```
tesserafin-server_1.0.0_win-x64/          <- the ONE top-level directory
├── tesserafin.exe                        <- 508 files: the self-contained publish
├── tesserafin.dll
├── tesserafin.runtimeconfig.json
├── hostfxr.dll, hostpolicy.dll, coreclr.dll, System.Private.CoreLib.dll
├── …
├── Resources/Configuration/logging.json  <- the shipped default template, not state
├── ServerSetupApp/
├── wwwroot/                              <- api-docs and diagnostics-ui, from the publish
├── web/                                  <- 2316 files, the accepted Web payload
│   └── …
├── ffmpeg/                               <- 27 files, the accepted runtime archive, extracted
│   ├── bin/ffmpeg.exe
│   ├── bin/ffprobe.exe
│   ├── LICENSES/…                        <- 22 component licences
│   ├── THIRD-PARTY-NOTICES.md
│   ├── build-configuration.txt
│   └── capability.json
└── licenses/
    ├── LICENSE                           <- the server's own licence
    ├── ffmpeg/sbom.cdx.json              <- retained BESIDE the runtime archive
    └── provenance.json                   <- the provenance manifest
```

`%ProgramFiles%\Tesserafin\Server\` in §9.1 is this directory. `%ProgramData%`
appears nowhere: configuration, database, cache and logs are always given by
argument, and the packer refuses to pack any of them (§4).

**Why the FFmpeg licences are not copied twice.** They already ship inside the
accepted runtime archive. Two copies of a licence file are two things that can
disagree, so the assembler instead hashes the *extracted* `THIRD-PARTY-NOTICES.md`
and `capability.json` against `noticesSha256` and `capabilitySha256`, and counts
the component licences against `licenceFileCount` — measured 22, as recorded.
The SBOM is the one description retained beside the archive rather than inside
it, so it is the one file taken from the verified retention unit and re-hashed
against `sbomSha256`.

---

## 2. How `SOURCE_DATE_EPOCH` is derived

It is a **required input** to the assembler. It is never read from the clock,
never derived from a tag, and never defaulted — `Z01` and `Z02` observe the
refusal, and `Z01`'s live proof shows that a copy of the packer which silently
substitutes `UtcNow` for a missing epoch does produce an archive, so the check is
load-bearing rather than decorative.

The workflow derives it once, from the commit being built:

```
git log -1 --format=%ct <the pull request head commit>
```

which is the committer time, and the same definition
`docker/version-contract.sh cmd_env` already uses for every other Tesserafin
artifact. Deriving it once and passing the same value to both assemblies is what
makes the two runs comparable by construction rather than by coincidence.

`<version>` is `1.0.0`, read from `SharedVersion.cs` — the same canonical source
`docker/version-contract.sh` reads, with the same MAJOR.MINOR.PATCH rule. No new
marketing version is invented.

### The MS-DOS second

A ZIP timestamp stores seconds/2, so it truncates an odd second to the even one
below. Committer times are odd about half the time. The packer's read-back check
therefore compares against the clamp **as a ZIP can hold it**, not against the
raw epoch; the untruncated comparison refused every odd commit time and would
have reddened one hosted run in two while looking like nondeterminism rather than
like arithmetic. `Z19` is the regression guard: it requires an odd epoch to pack,
to store an even second, and to agree byte for byte with the even epoch that
lands in the same MS-DOS second.

---

## 3. What makes the bytes a function of the contents

`System.IO.Compression.ZipArchive` in `Create` mode over a seekable stream, with
every property that could otherwise vary set explicitly:

| property | value | why it is set rather than inherited |
| --- | --- | --- |
| entry order | `[Array]::Sort(..., StringComparer::Ordinal)` over forward-slash relative paths | `Sort-Object` is culture-sensitive and directory enumeration is the filesystem's |
| mtime | the clamp, in UTC, on every entry | reading the file's own makes the archive depend on when it was staged |
| modes | `0755` for `.exe`, `0644` otherwise, in the high 16 bits of `ExternalAttributes` | the same rule `ci/windows/ffmpeg/package.py` already used to build the inner archive |
| MS-DOS attribute word | zero | reading the real attributes back would make the archive depend on the filesystem |
| directory entries | none | same, and the precedent above emits none |
| compression | `CompressionLevel::Optimal`, stated | the level is an input to the compressed bytes just as the content is |

The archive is then **read back** before the packer returns: entry count, entry
order, the clamp on every entry, and a SHA-256 of every unpacked entry against
the staged file it came from. A packer whose output cannot be read is not a
packer.

Two measurements, not one argument: the entry order and the clamp are each shown
to be load-bearing by handing the same stages to a mutated copy of the packer and
requiring it to produce **different** bytes (`Z04`, `Z05`).

---

## 4. Fail-closed

No archive is produced, and none is left behind, when:

| refusal | control |
| --- | --- |
| `SOURCE_DATE_EPOCH` missing | `Z01` |
| `SOURCE_DATE_EPOCH` zero | `Z02` |
| `SOURCE_DATE_EPOCH` outside what a ZIP can represent | `Z03` |
| a second top-level directory under the stage | `Z08` |
| a state file about to be packed | `Z06` |
| the Web tree does not hash to `WEB_PAYLOAD_SHA256` | assembler, §5 |
| the inner FFmpeg archive does not hash to `runtimeSha256` | assembler, §5 |
| the publish tree is not self-contained `win-x64` | assembler, §5; audited by `Z20` |

**State** is named shapes only: `*.db`, `*.db-wal`, `*.db-shm`, `*.db-journal`,
`*.log`; the server's configuration documents by name (`network.xml`,
`system.xml`, `encoding.xml`, …); and the operator-owned directories of §9.1
(`config`, `data`, `cache`, `log`, `transcodes`, `plugins`, `metadata`, …) when
they appear directly under the package root. A rule broad enough to catch
"anything that looks like configuration" catches
`Resources/Configuration/logging.json`, which is the shipped default template and
not state, and the only way to keep such a rule green is to loosen it until it
would miss the real thing.

---

## 5. The digest checks that are assembler refusals rather than controls

Three of the ruling's fail-closed conditions cannot be observed on a runner with
no network, because reaching them means acquiring the real payload and the real
runtime first. They are **assembler** checks, audited at their source and proven
to agree with the ruling by `Z07`:

* the extracted **and then the staged** Web tree must hash to
  `4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f`. The staged
  copy is hashed a second time, at the epoch the **web** build recorded for
  itself rather than at this commit's — the digest of an input must not move when
  the server commit moves, which is the rule `ci/package/lib.sh` states;
* the inner FFmpeg archive must hash to
  `f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e`, and the
  committed acceptance manifest must pin the same value;
* the publish tree must carry `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll` and
  `System.Private.CoreLib.dll`; its `runtimeconfig.json` must declare no shared
  `framework`/`frameworks` and must declare `includedFrameworks`; and
  `tesserafin.exe` must be a PE image with machine `0x8664`. Three independent
  statements, because each alone has a way of being true by accident.

`Z20` audits that third one at its source rather than behaviourally: reaching it
means publishing the server first, and a control suite that had to build the
server to prove a check exists would be a build, not a control. Its INERT-proof
removes each rule from a disposable copy and requires the audit to report every
one.

---

## 6. The controls

`ci/windows/w2/zip-controls.py`, 20 rostered controls plus `ROSTER` and
`RESTORE`. Every named refusal is observed live against the **real** assembler
over a disposable stage, and every audit is paired with a live INERT-proof — a
mutated copy of the assembler with that one check defeated, required to do the
thing the real one refused. A control whose mutation no longer applies reports
`INERT` rather than shrinking the suite into a smaller green one; `ROSTER` turns
a deleted control into a `RED`, which was verified by deleting `Z16` from a
disposable copy of the tree.

`Z13` carries the raw-byte pin of this slice's workflow and re-applies the exact
permission mutation that walked past W2-A0's first workflow audit — the one that
demotes `pull_request:` and `contents: read` to comments while granting
`write-all` at both scopes and adding a second job. Applied to the real workflow
it reports a **named** RED, not a pin-only one:

```
Z13 RED grants write-all; triggers on 'push'; declares the unauthorised job
        'exfiltrate'; does not request contents: read at any scope; does not
        watch the whole ci/windows/w2 tree; is not the pinned workflow …
```

`Z14` pins all four frozen inputs — `consume-web-payload.ps1`,
`pkg-tree-digest.py`, `consume.ps1` and `accepted-runtime.json`. W2-A2 drives
them and audits them, and authors none of them.

---

## 7. W2-A1-V1 NB-1, closed here

A1's `paths:` filter names three files individually, so a second file added under
`ci/windows/w2/` could be reviewed without ever running the proof that covers it.
The ruling scheduled that as a constraint on the next workflow to touch that
tree. This workflow's filter is:

```yaml
paths:
  - 'ci/windows/w2/**'
  - '.github/workflows/w2-windows-server-zip.yml'
  - 'docs/distribution/W2-A2-server-zip.md'
  - 'ci/windows/runtime-retention/consume.ps1'
  - 'ci/windows/runtime-retention/accepted-runtime.json'
  - 'SharedVersion.cs'
```

`Z16` asserts it. **NB-1 is closed for this workflow.** A1's own filter is left
frozen, as the ruling directs.

---

## 8. Amendment W2-A2-F18: A1's second-consumer check names the assembler

`ci/windows/w2/ffmpeg-consume-controls.py` treated every `.ps1` under
`ci/windows/w2/` except `consume-web-payload.ps1` as "a second consumer". That
is the right refusal for a second FFmpeg or Web consumer, and the wrong one for
the ZIP assembler this slice is authorised to add: with
`ci/windows/w2/assemble-server-zip.ps1` present, the A1 suite read

```
W2-A1 controls: 21 PASS, 1 RED, 0 INERT
  F18 RED ci/windows/w2/assemble-server-zip.ps1 is a second consumer
```

The owner ruling **W2-A2-F18** on #256 amends this slice: it authorises the
fifth path `ci/windows/w2/ffmpeg-consume-controls.py` and exactly one change
inside it — `F18` allows `assemble-server-zip.ps1` **by exact name**, the same
way it already allows `consume-web-payload.ps1`. Widening `F18` to "any `.ps1`
under `w2/`", deleting it, or touching `F01`-`F17`, `F19`, the roster or the
frozen-consumer byte pins is not authorised, and none of it was done. The
one-line allowlist is the whole edit.

`F18` therefore still REDs any other new `.ps1` in that directory — a planted
`ci/windows/w2/ffmpeg-consume.ps1` is named as a second consumer — and the A1
suite now reads 22 PASS, 0 RED, 0 INERT.

The assembler is not an FFmpeg consumer and not a registry consumer: it invokes
the frozen consumers and adds none of its own, and it must never grow a
`-Reference`. `Z09`, `Z10` and `Z12` own that property behaviourally and by
source audit; A1's `F18` is a directory-listing heuristic, not a statement about
behaviour.

`ci/windows/runtime-retention/consume.ps1` and `accepted-runtime.json` remain
frozen and byte-identical, so A1's `F16` pins are untouched. A1's own `paths:`
filter watches `ci/windows/w2/ffmpeg-consume-controls.py`, so the W2-A1 workflow
runs on this pull request with the amended check.

---

## 9. What this slice deliberately does not do

* **§8.1 two-runner identity is not claimed.** Two assemblies on one runner is
  the authorised evidence. The publish embeds the checkout path in its assemblies
  and PDBs, which is identical for both assemblies on one runner and would not be
  across two machines; a cross-machine claim would need a `PathMap` and a second
  runner, and neither is authorised here.
* **The intermediate `obj/` tree is shared** between the two assemblies. This
  slice proves the *packer* is a function of its inputs, not that the compiler is.
* **Nothing is started.** `tesserafin.exe` is not run, the tree is not relocated,
  no port is discovered, `/` is not requested and no service is registered.
  Relocation is §2.3's proof and belongs to a later slice. `Z18` asserts that
  neither the assembler nor the workflow registers a service, starts a process or
  reaches an HTTP endpoint.
* **No first-party service script.** §6's last bullet is deferred.
* **No publication.** No package write, no release asset, no Actions artifact —
  so the ZIP can never become a production input to another job (`Z11`).
* **No corresponding-source archive inside the ZIP.** §6's content list does not
  name it, and it is 216 MB. The binary ships with its licences and notices, and
  `licenses/provenance.json` records the retained source's path, its digest and
  its decompressed-stream digest under the same immutable runtime reference.
* **The provenance manifest is not validated against
  `ci/package/provenance.schema.json`.** That schema's `packageFormat` enum is
  `deb`/`rpm`/`tar.gz` and the file is not an authorised path in this slice, so
  the ZIP carries its own `schemaVersion: 1` manifest named
  `tesserafin-windows-server-zip` instead. Unifying the two is later work.
* **`W2` is not accepted.** This is one slice of it.

---

## 10. Evidence

The two hosted `windows-latest` ZIP hashes are **cited in the pull request**:
this document is committed before the first hosted run exists, and a hash written
here before it was measured would be a guess.

No archive digest is written into this repository at all, and that is a property
rather than an omission: `licenses/provenance.json` records `serverCommit`, so
the archive's digest is a function of the commit that produced it and any digest
committed alongside the code it describes would be describing a different commit.

What was measured locally, running the same script under `pwsh` on Linux against
the real accepted payload and the real accepted runtime, at
`SOURCE_DATE_EPOCH=1756900001`:

| what | value |
| --- | --- |
| entries | 2868 |
| uncompressed | 426.6 MiB |
| archive bytes | 181 684 122 |
| two assemblies, two work trees, two output trees | byte-identical |

A local digest would not be the hosted one in any case: the .NET apphost differs
between a Linux and a Windows publish, the "version made by" host byte in a ZIP
local header is the packing platform's, and the checkout path embedded in the
assemblies differs. One-runner identity is the claim; cross-platform identity is
not.

Local suite results at the commit this document ships with:

```
W2-A2 controls: 22 PASS, 0 RED, 0 INERT
W2-A0 controls: 44 PASS, 0 RED, 0 INERT
W2-A1 controls: 22 PASS, 0 RED, 0 INERT   (F18 amended, §8)
```
