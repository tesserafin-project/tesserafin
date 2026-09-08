# W2-A6 — The directory sanity marker rename

Tracker: [#256](https://github.com/tesserafin-project/tesserafin/issues/256).
Umbrella: #234. Base master:
`147687f323cdc77f545520a095abf2f7c2a5df1e`, which is W2-A5 as accepted.

This slice renames the directory sanity markers and migrates existing
installations onto the new spelling. It is the last of the marker items W0 left
open. **W2 is not accepted by this document**, no release is made, and the
service and SCM boundary remains W3's.

---

## 1. What W0 asked for, and what it did not say

[W0 §2.3](W0-windows-server.md) records the defect in the server that its probe
turned up:

> `BaseApplicationPaths.MakeSanityCheckOrThrow` writes a marker file into each
> of the configuration, cache, log and data directories and refuses to start if
> it finds the wrong one. It is a good guard — it is what caught a mis-split
> path immediately rather than fifty lines later — but the markers are still
> named **`.reefin-config`, `.reefin-log`** and so on, from before the rename.
> On Linux they are hidden dotfiles inside package-managed directories. On
> Windows there are no hidden dotfiles by convention, so they become visible
> files with the old product name inside `%ProgramData%\Tesserafin\Server\`.
> W2 should rename them, and must do so with a migration that recognises the old
> marker, or every existing installation fails its own sanity check on upgrade.

Two things follow from that paragraph, and only two. It names the **need** for a
rename and the **migration** requirement. It does **not** name a target
spelling: `.reefin-config`, `.reefin-log` and `.reefin-*` are the only marker
strings anywhere in the document, and its gap table still reads

> | renamed directory sanity markers | still `.reefin-*`, and visible files on Windows (§2.3) |

The target names in §2 below are therefore **not** read off W0. They are the
owner's, ruled on #256 (`W2-A6-NAMES`, amended by `W2-A6-PREFIX AND TESTS`), and
this document is the only place in the corpus that records them. W0 itself is
unchanged by this slice.

---

## 2. The target table

One prefix swap, applied where the filename is built:

| old | new |
| --- | --- |
| `.reefin-config` | `.tesserafin-config` |
| `.reefin-log` | `.tesserafin-log` |
| `.reefin-cache` | `.tesserafin-cache` |
| `.reefin-data` | `.tesserafin-data` |
| `.reefin-plugin` | `.tesserafin-plugin` |
| `.reefin-root` | `.tesserafin-root` |
| `.reefin-transcode` | `.tesserafin-transcode` |

The new spelling is written lowercase, NFC, ASCII, and is the only spelling any
code path writes.

### 2.1 Seven stems, not five, and why the count is a property of the callers

`W2-A6-NAMES` originally named five, from the six call sites in
`BaseApplicationPaths.MakeSanityCheckOrThrow`. That inventory was incomplete.
`CreateAndCheckMarker(string path, string markerName, bool recursive = false)`
is on the public `IApplicationPaths` interface
(`Tesserafin.Common/Configuration/IApplicationPaths.cs:111`) and builds the
filename as *prefix + stem*, where **the stem is supplied by the caller**. The
first-party callers at the base master are:

| stem | call site | note |
| --- | --- | --- |
| `config` `log` `plugin` `data` `cache` | `Tesserafin.Server.Core/AppBase/BaseApplicationPaths.cs` | six calls, five stems; `data` is written for both `ProgramDataPath` and `DataPath`, which stay two directories |
| `root` | `Tesserafin.Server.Core/ServerApplicationPaths.cs` | in the `override` that `Tesserafin.Server/Program.cs` actually runs |
| `transcode` | `Tesserafin.Common/Configuration/EncodingConfigurationExtensions.cs` | the one caller passing `recursive: true`; its path is operator-configurable via `TranscodingTempPath` |

`Tesserafin.Server.Core/AppBase/BaseConfigurationManager.cs` also re-writes the
`cache` marker when the cache path changes. That is the same stem, not an
eighth name.

The seven above are **not an allowlist**. Because the swap is applied to the
prefix and not to a set of known stems, a caller of the public interface passing
any other stem gets the same new prefix and the same migration. That is the
property that makes "no first-party code writes `.reefin-*`" true, and keeps it
true for a stem added later.

---

## 3. The migration

Decided **per directory, independently**, so a partially migrated installation
converges in a single pass:

1. the new marker is present — nothing to do;
2. otherwise the old marker is present — **write the new one, then remove the
   old one**;
3. otherwise — write the new one.

The order in step 2 is the whole of the contract. Removing the old marker first
and then failing to write the new one — a full disk, a revoked ACL, a process
killed between the two — leaves a state root with no marker at all, which is
indistinguishable from a fresh directory. The old marker is never removed until
the new one is on disk. If the removal itself fails, the new marker is already
in place and the stale one is cosmetic: it is recognised again, and its removal
retried, on the next start.

Nothing is copied. These are empty sanity markers, not payload.

### 3.1 Recognition is wider than the write path

A marker that differs only in case, or only in Unicode normalisation, is the
same marker on every file system, so:

* enumeration is case-insensitive (`MatchCasing.CaseInsensitive`), which is what
  makes `.REEFIN-config` recognisable on a case-sensitive file system where a
  `.reefin-*` glob would miss it;
* comparison normalises to NFC, so NFC and NFD spellings are one marker. On these
  ASCII names the two forms are identical, so this costs nothing and closes the
  case by construction rather than by assumption. A file name that is not valid
  Unicode cannot be normalised; it is compared as it came off the file system
  rather than failing a state root over it.

A case-only difference **between the two prefixes** is not "already migrated":

* `.REEFIN-config` is the **old** marker, and is migrated;
* `.TESSERAFIN-config` is the **new** marker, already present, and is left alone
  rather than duplicated with a second file.

### 3.2 A marker belonging to a different root still fails the check

The guard W0 called good is unchanged. A directory holding a marker for another
state root — `.tesserafin-log` in the configuration directory, or `.reefin-log`
there — still throws, before anything is written.

### 3.3 One correction that is not cosmetic

Enumeration now sets `AttributesToSkip = FileAttributes.None` explicitly. The
markers are dotfiles, and .NET reports dotfiles as `FileAttributes.Hidden` on
Unix. The default `EnumerationOptions` skips hidden entries, so an
`EnumerationOptions`-based enumeration that did not say this would have found no
markers at all on Linux — the sanity check would have passed silently on every
tree, migrated nothing, and detected no wrong marker. The old
`SearchOption`-based overload used `EnumerationOptions.Compatible`, which does
not skip them. `IgnoreInaccessible = false` preserves the same overload's
behaviour of surfacing an unreadable directory rather than reporting it
marker-free.

---

## 4. The controls

Eleven tests in
`tests/Tesserafin.Server.Implementations.Tests/AppBase/BaseApplicationPathsMarkerTests.cs`.
That project is the only test project at the base master referencing
`Tesserafin.Server.Core`; nothing at the base master asserted the marker name at
all, so the rename had no existing assertion to update.

The marker spellings are written in the tests as **literals**, not read from the
production constants. A test comparing against the constant passes under a
mutated prefix, which is precisely the regression these exist to catch.

The ordering control does not race the file system. It places a *directory* on
the new marker's path so the write cannot succeed, then asserts the pre-rename
marker is still there. An implementation that removed the old marker first would
have destroyed it before failing, deterministically and with no timing window.

### 4.1 Observed RED

Each mutation was applied to the committed tree, the solution rebuilt, and the
suite re-run. Every one failed on assertions, not on a build error.

| control | mutation | observed |
| --- | --- | --- |
| `C1` — a new install writes the old name | `MarkerPrefix` reverted to `.reefin-` | **RED**, 9 of 11 failing, including `MakeSanityCheckOrThrow_FreshTree_WritesOnlyNewMarkers` |
| `C2` — migration removes the old marker before the new one exists | the removal loop moved ahead of the write | **RED**, exactly `CreateAndCheckMarker_NewMarkerCannotBeWritten_LeavesTheLegacyMarkerInPlace` |
| `C3a` — a first-party call site still writes the old name (`transcode`) | the `transcode` stem special-cased back to the old prefix | **RED**, all three transcode and recursive tests |
| `C3b` — a first-party call site still writes the old name (`root`) | the `root` stem special-cased back to the old prefix | **RED**, both `MakeSanityCheckOrThrow` tests |
| `C4` — an old marker counts as already migrated, no new file written | the old marker deleted and the method returned early | **RED**, 6 of 11, including `CreateAndCheckMarker_LegacyMarker_CreatesTheNewFileRatherThanOnlyRemovingTheOld` |

`C2` failing exactly one test, and that test being the ordering one, is the point:
it shows the control reaches the ordering gate and nothing else.

### 4.2 The earlier slices

Re-run on this head, all green at their accepted counts:

| suite | result |
| --- | --- |
| W2-A0 `web-payload-controls.py` | 44 PASS, 0 RED, 0 INERT |
| W2-A1 `ffmpeg-consume-controls.py` | 22 PASS, 0 RED, 0 INERT |
| W2-A2 `zip-controls.py` | 22 PASS, 0 RED, 0 INERT |
| W2-A3 `start-controls.py` | 19 PASS, 0 RED, 0 INERT |
| W2-A4 `two-runner-controls.py` | 26 PASS, 0 RED, 0 INERT |
| W2-A5 `service-script-controls.py` | 25 PASS, 0 RED, 0 INERT |

A1 requires `--oras <path>`; without a client `F06`–`F09` report RED because a
mutated digest cannot be measured, not because a gate failed.

`ci/secret-scan.sh --mode tree`: **CLEAN**.

### 4.3 The real server, started twice

The unit controls exercise `ServerApplicationPaths` directly. They cannot see the
two call sites that only a real start reaches: `BaseConfigurationManager`'s cache
re-check, and `GetTranscodePath`, which is the sole `recursive: true` caller and
whose directory does not exist until the server creates it. Both trees below were
started with the actual `Tesserafin.Server` entry point.

| | pre-rename tree | fresh tree |
| --- | --- | --- |
| seeded before start | all eight `.reefin-*` markers | nothing |
| `/` answered | **302** after 11 s | **302** after 8 s |
| port | 8096, read from the process's own listening TCP ports | same |
| still running 3 s after answering | yes | yes |
| `.reefin-*` on disk after start | **none** | **none** |
| `.tesserafin-*` on disk after start | all eight | all eight |

The eight, in both cases:
`config/.tesserafin-config`, `log/.tesserafin-log`, `cache/.tesserafin-cache`,
`cache/transcodes/.tesserafin-transcode`, `data/.tesserafin-data`,
`data/data/.tesserafin-data`, `data/plugins/.tesserafin-plugin`,
`data/root/.tesserafin-root`.

`cache/transcodes/.tesserafin-transcode` appearing in the fresh column is the
part no unit test could have produced: the directory is created during startup,
and its marker is written by the one caller outside `BaseApplicationPaths`.

**What this run does not claim.** It is a framework-dependent Debug build on a
Linux host started in place, not the win-x64 ZIP and not a relocation. Its `-w`
pointed at a local development web tree, not the payload pinned by
`WEB_PAYLOAD_SHA256`, so W0 §2.4's hashed-bundle assertion is **not** made here.
The measurement is W0 §2.3 readiness and the marker set, and nothing else.

### 4.4 The hosted relocate-start job cannot fire on this head

`.github/workflows/w2-windows-relocate-start.yml` triggers on `pull_request`
alone — there is no `workflow_dispatch` — and filters on
`ci/windows/w2/**`, its own file, `docs/distribution/W2-A3-relocate-start.md`,
`ci/windows/runtime-retention/consume.ps1`,
`ci/windows/runtime-retention/accepted-runtime.json` and `SharedVersion.cs`.
This slice touches none of them, so the job is not queued on this head.

That filter is a gap worth a separate ruling, and this document does not close
it: **the relocation proof does not watch the server C# files whose behaviour it
exercises.** A change to `BaseApplicationPaths` alters what a relocated tree
writes into its state roots on first start, and no relocation proof re-runs for
it. Widening the filter, or adding a dispatch trigger, would edit a frozen
W2-A3 path that this slice is not authorised to touch.

---

## 5. Non-goals

This slice does not:

* change `docs/distribution/W0-windows-server.md`, or the W2-A1..A5 documents,
  which record what those slices did **not** do and stay accurate as written;
* add a PowerShell script under `ci/windows/w2/`, or change the service script,
  the `F18`/`T16`/`M12` amendments, or the assembler;
* touch the `reefin-plugin-*.svg` embedded resource names or the `internal.reefin`
  hostname fixture, neither of which is a directory sanity marker;
* register a service, pass `--service`, publish, tag or release;
* accept W2, or close #256.
