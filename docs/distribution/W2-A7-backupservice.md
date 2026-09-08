# W2-A7 — BackupService uses the new marker prefix

Amended in flight by **W2-A7-STEM**. Read the amendment before the original
ruling: the original slice was written against a marker prefix that this file
never carried, and the amendment replaces both the target stem and the control
literal.

Base master: `e572db1f7fd27aa3987e183b7d96834a928866ea` (W2-A6 accepted).
Tracker: #256, which stays open. This slice does not accept W2.

## What the ruling assumed, and what is actually there

The ruling authorised a fix at `Tesserafin.Server.Core/Backup/BackupService.cs`
and instructed a re-resolution rather than a search if `Backup/` was wrong. It
is wrong. At the base master there is exactly one production `BackupService.cs`:

| | |
| --- | --- |
| Authorised path | `Tesserafin.Server.Core/Backup/BackupService.cs` |
| Real path | `Tesserafin.Server.Implementations/FullSystemBackup/BackupService.cs` |

The other `*BackupService*` paths are `Tesserafin.Controller/SystemBackupService/`
— `BackupManifestDto.cs`, `BackupOptionsDto.cs`, `BackupRestoreRequestDto.cs`
and `IBackupService.cs`. None of them constructs a filename. No second
production file was edited.

The ruling also assumed the leftover spelling was `.reefin-`, the A6 marker
prefix. It is not. At the base master, line 321 read:

```csharp
var backupPath = Path.Combine(backupFolder, $"reefin-backup-{manifest.DateCreated.ToLocalTime():yyyyMMddHHmmss}.zip");
```

`reefin-backup-`, with **no leading dot**. Three consequences follow, and they
are why the slice was stopped and amended rather than implemented as written:

1. **The ruling's self-proof grep was already clean.** At the base master,
   `git grep -n '\.reefin-' -- '*.cs'` matched nothing in `BackupService.cs`.
   Its only hits were `Tesserafin.Server.Core/AppBase/BaseApplicationPaths.cs:33`
   (`LegacyMarkerPrefix = ".reefin-"`, the A6 migration constant, which must
   stay), five `Tesserafin.Providers/Plugins/*/Plugin.cs` namespace resource
   strings, and the A6 test seeds in
   `tests/Tesserafin.Server.Implementations.Tests/AppBase/BaseApplicationPathsMarkerTests.cs`.
   The grep could not discriminate before from after.
2. **A control keyed on `.reefin-` would have been inert.** It passes on an
   unmodified tree — a green that proves nothing, which is the failure mode the
   ruling's "RED control" language exists to prevent.
3. **This is not a marker.** The ruling's own second branch applies: "If it is
   only a string in a zip entry or log line, replace the spelling; do not
   pretend it was a marker." So there is no migration and no `old → new` order
   to mirror from A6.

## W2-A7-STEM

The amendment settles the stem question the ruling left open — A6's prefix is a
dotted hidden-file marker prefix, and this is an operator-visible archive name,
so applying `.tesserafin-` verbatim would have produced a hidden zip:

| | |
| --- | --- |
| Path | `Tesserafin.Server.Implementations/FullSystemBackup/BackupService.cs` |
| Write | `tesserafin-backup-{ts}.zip` |
| Control literal | `reefin-backup-` |
| Migration | none |
| Leading dot | none |

## The change

One production line. `BackupService.cs:321` now reads:

```csharp
var backupPath = Path.Combine(backupFolder, $"tesserafin-backup-{manifest.DateCreated.ToLocalTime():yyyyMMddHHmmss}.zip");
```

Nothing else in that file changed. The `_reefinDatabaseProvider` field, its
constructor parameter and its doc comment are identifier spellings, not
filenames, and are out of scope for this slice.

## Why no compatibility glob is needed

The read side never keys on the stem. In the same file:

- `BackupService.cs:517` — `Directory.EnumerateFiles(_applicationPaths.BackupPath, "*.zip")`
  enumerates by extension alone;
- `RestoreBackupAsync` restores whatever path it is handed.

So archives already on disk under `reefin-backup-*.zip` stay listable and stay
restorable. Nothing is renamed on disk, nothing is migrated, and no existing
backup is orphaned. Only newly created archives take the new stem.

## The control

`tests/Tesserafin.Server.Implementations.Tests/FullSystemBackup/BackupArchiveNamingTests.cs`,
`CreateBackupAsync_WritesArchiveUnderTheCurrentStem`.

It builds a real `BackupService` over a temporary SQLite database — the same
harness shape as the neighbouring `ContentPackBackupRoundTripTests` — calls
`CreateBackupAsync`, and asserts on two separate observations:

- the name reported by `BackupManifestDto.Path`, and
- the single `*.zip` that actually landed in the mocked `BackupPath`,

requiring both to start with `tesserafin-backup-`, to contain no
`reefin-backup-`, and to agree with each other. Asserting the file on disk as
well as the manifest is what makes it fail for a service that writes one name
and reports another; a source-text scan could not tell those apart.

The control is RED at the base master and green at this head.

## Self-proof

- `git grep -n '\.reefin-' -- '*.cs'` — no production filename prefix. Eighteen
  hits survive at this head, unchanged from the base master:

  - `Tesserafin.Server.Core/AppBase/BaseApplicationPaths.cs:33`,
    `private const string LegacyMarkerPrefix = ".reefin-";`
  - five `Tesserafin.Providers/Plugins/*/Plugin.cs` namespace resource strings
    (`AudioDb:33`, `MusicBrainz:71`, `Omdb:33`, `StudioImages:55`, `Tmdb:49`)
  - twelve seeds and constants in
    `tests/Tesserafin.Server.Implementations.Tests/AppBase/BaseApplicationPathsMarkerTests.cs`

  The ruling's exemption list names "Plugin.cs namespace strings and test
  hostnames" and does not literally name the first of these, so it is called out
  rather than left to be graded as a failed self-proof. `LegacyMarkerPrefix` is
  the A6 read-side migration constant — it is the *old* prefix
  `BaseApplicationPaths` looks for in order to migrate a pre-rename tree to
  `.tesserafin-*`. It is not a filename this server writes, and deleting it
  would break the A6 migration that was accepted at the base master. It stays.

- `git grep -n 'reefin-backup-' -- '*.cs'` — two hits, both in the control:
  `BackupArchiveNamingTests.cs:27` (the XML doc comment quoting the pre-rename
  stem) and `BackupArchiveNamingTests.cs:45` (the `LegacyStem` constant the
  assertion is made against). No production C# file constructs the old stem.
  This document also quotes the pre-rename line, but it is Markdown and outside
  the `*.cs` pathspec.

- The A0–A5 suites are unchanged by this slice; no file they own was touched.
  `grep -rlniE 'BackupService|reefin-backup' ci/windows/ .github/workflows/`
  returns nothing, so no W2 packaging gate is coupled to this stem.

## What this slice does not do

- It does not accept W2. #256 stays open.
- It does not touch the A3 workflow path filter or
  `.github/workflows/w2-windows-relocate-start.yml`.
- It does not touch F18 / T16 / M12, the assembler or the `.ps1`.
- It does not edit the W0 or W2-A1..A6 documents.
- It publishes nothing.
- It does not rename identifiers, only the written filename.
