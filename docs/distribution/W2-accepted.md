# W2 — the accepted portable Windows server ZIP surface

Tracker: [#256](https://github.com/tesserafin-project/tesserafin/issues/256).
Umbrella: [#234](https://github.com/tesserafin-project/tesserafin/issues/234).
Accepted master at this closeout:
`fb43b1627f1f77abf88777344670c33a7464ecbd`, which is W2-A7 as accepted.
Frozen W2 starting master: `aac506ed751af520cc7ba459341cd8abf22be6cf`, which is
W1-A5 as accepted (#255).

This document ships no behaviour. It records which slices W2 landed, at which
commits, what each of them proved in its own words, and what W2 did **not**
prove. Every claim here is already made, and already reviewed, in the slice
document and pull request the table names. Nothing new is claimed, and no
earlier document is amended.

---

## 1. The eight slices, with SHAs

Each row is one accepted slice. `Base` is `origin/master` immediately before the
landing; `Accepted head` is the landing SHA, which is also the SHA the
independent review read and the SHA the required checks ran at. Every landing
was a true fast-forward: `git merge --ff-only` then `git push origin master`,
with the `gh pr merge --rebase` fallback used by none of them, so each pull
request records its own accepted head as its merge commit and no SHA was
rewritten.

| Slice | What it proved | Base | Accepted head | PR | Commits | Slice document |
| --- | --- | --- | --- | --- | --- | --- |
| **A0** | web payload pin / consume | `aac506ed751af520cc7ba459341cd8abf22be6cf` | `3d0c80047ce418ec4255ff3d788fe8d48a3da3e3` | [#257](https://github.com/tesserafin-project/tesserafin/pull/257) | 5 | [`W2-A0-web-payload.md`](W2-A0-web-payload.md) |
| **A1** | FFmpeg runtime consume | `3d0c80047ce418ec4255ff3d788fe8d48a3da3e3` | `d04689277c61e59e9b15815cb6de92ee67ef458e` | [#258](https://github.com/tesserafin-project/tesserafin/pull/258) | 2 | [`W2-A1-ffmpeg-runtime.md`](W2-A1-ffmpeg-runtime.md) |
| **A2** | deterministic ZIP | `d04689277c61e59e9b15815cb6de92ee67ef458e` | `5ba584383c7381bea1c23708a2e3781bb428326b` | [#259](https://github.com/tesserafin-project/tesserafin/pull/259) | 4 | [`W2-A2-server-zip.md`](W2-A2-server-zip.md) |
| **A3** | relocate and start (exe, not SCM) | `5ba584383c7381bea1c23708a2e3781bb428326b` | `8718096aa5f56d33a5b4fad91935ae89037a6e7b` | [#260](https://github.com/tesserafin-project/tesserafin/pull/260) | 4 | [`W2-A3-relocate-start.md`](W2-A3-relocate-start.md) |
| **A4** | two-runner IDENTICAL | `8718096aa5f56d33a5b4fad91935ae89037a6e7b` | `5e307ce36e5baa4192f178f0fb8de2e58efe2afb` | [#261](https://github.com/tesserafin-project/tesserafin/pull/261) | 4 | [`W2-A4-two-runner.md`](W2-A4-two-runner.md) |
| **A5** | service script in the ZIP | `5e307ce36e5baa4192f178f0fb8de2e58efe2afb` | `147687f323cdc77f545520a095abf2f7c2a5df1e` | [#262](https://github.com/tesserafin-project/tesserafin/pull/262) | 16 | [`W2-A5-service-script.md`](W2-A5-service-script.md) |
| **A6** | `.reefin-` → `.tesserafin-` markers | `147687f323cdc77f545520a095abf2f7c2a5df1e` | `e572db1f7fd27aa3987e183b7d96834a928866ea` | [#263](https://github.com/tesserafin-project/tesserafin/pull/263) | 3 | [`W2-A6-reefin.md`](W2-A6-reefin.md) |
| **A7** | `tesserafin-backup-{ts}.zip` | `e572db1f7fd27aa3987e183b7d96834a928866ea` | `fb43b1627f1f77abf88777344670c33a7464ecbd` | [#264](https://github.com/tesserafin-project/tesserafin/pull/264) | 2 | [`W2-A7-backupservice.md`](W2-A7-backupservice.md) |

Forty commits, zero merge commits: `git rev-list --merges --count
aac506ed751af520cc7ba459341cd8abf22be6cf..fb43b1627f1f77abf88777344670c33a7464ecbd`
is `0`, and every base and accepted head above is an ancestor of
`fb43b1627f1f77abf88777344670c33a7464ecbd`. The chain is continuous — each
slice's base is the previous slice's accepted head, with no unrelated commit
between them.

Each slice was accepted individually, by its own `OWNER RULING — W2-A<n>-READY-FF`
on #256, after an independent hostile review verdict of ACCEPT with zero
blocking findings. This document is the union of those eight acceptances and
adds no ninth.

### A7's spelling, precisely

A7 is **not** a marker rename. Its whole production delta is one line,
`Tesserafin.Server.Implementations/FullSystemBackup/BackupService.cs:321`,
swapping the backup archive **stem** `reefin-backup-` to `tesserafin-backup-`.
No leading dot, no marker, no migration, and no rename of anything on disk.
The `W2-A7-backupservice.md` title says "marker prefix" because the original
ruling assumed that spelling; `W2-A7-STEM` amended it in flight, and the
document records the correction.

---

## 2. What W2 does not claim

The following are named here so that no reader infers them from the table
above. None of them is proved by any accepted slice.

* **W3's `--service` boundary and error 1053.** Unchanged at this master. A5
  registers no service and starts none; §4's error 1053 is W3's.
* **An SCM start as hosted evidence.** No Service Control Manager runs on any
  runner in any accepted slice. A5's service verbs are audited over the
  PowerShell AST and by name, never executed.
* **The A3 relocate-start path filter watching server C#.** It does not, and W2
  never claimed it did. See §3.
* **Publication.** No MSI, no tag, no release asset, no registry push, no
  package write. Nothing produced by W2 has been published.
* **Linux unit feature-parity beyond W0.** Out of scope for W2 and not measured
  by any slice here.

---

## 3. Residuals — named, not repaired

These are carried forward exactly as the accepting rulings left them. None is a
condition of any acceptance already granted, and recording them here neither
repairs them nor authorizes a repair.

### A5 — `NB-1`..`NB-4` and the `M12` limit

From #262's retained-limitations section, which `W2-A5-READY-FF` retains and
states do **not** authorize an R6:

* **NB-1** — `M20` does not falsify when the assembler stops staging the script.
* **NB-2** — `sc.exe binPath=` quoting is unobserved.
  `$PSNativeCommandArgumentPassing = 'Standard'` with an embedded-quote
  `binPath=` is the correct construction, but it is unmeasured, and
  `#Requires -Version 7.2` predates that mode becoming the default in 7.3.
* **NB-3** — `Get-ServiceRecord` parses a `STATE` label `sc.exe` may localise.
* **NB-4** — the fold is permissive for the `Invoke-Sc` allowlist, and the diff
  argues only the refusing direction. Measured on Linux `pwsh` 7.6.5 only; the
  Windows-side module export set is not verified. Graded NB because the argument
  is absent from the file, not because the property fails.

The **`M12` limit** is the shape of the control itself. `M12` asserts that "the
plan and the verbs read one definition of the SCM calls" by matching command
names — four names, compared `OrdinalIgnoreCase` after reduction to an
unqualified name. It is a static audit over the script's text and AST. Nothing
in A5 exercises `-Plan` or any verb on Windows, so `M12` cannot observe what the
verbs actually do to a Service Control Manager; it can only observe that the
plan and the verbs read the same definition.

### A3 — the workflow path filter gap

`.github/workflows/w2-windows-relocate-start.yml` fires on `pull_request` for:

```
ci/windows/w2/**
.github/workflows/w2-windows-relocate-start.yml
docs/distribution/W2-A3-relocate-start.md
ci/windows/runtime-retention/consume.ps1
ci/windows/runtime-retention/accepted-runtime.json
SharedVersion.cs
```

No server C# path is in that list. A6 changed
`Tesserafin.Server.Core/AppBase/BaseApplicationPaths.cs` and A7 changed
`Tesserafin.Server.Implementations/FullSystemBackup/BackupService.cs`, so
neither head could queue the relocate-and-start proof. #263 declared this rather
than claiming a proof, and the run list at both `e572db1f7f` and `fb43b1627f`
carries no relocate-start entry. The filter was deliberately **not** widened in
either loop; doing so is unauthorized here and needs its own ruling.

### A7 — assert order in the new test

**NB-1** — assert ordering in
`tests/Tesserafin.Server.Implementations.Tests/FullSystemBackup/BackupArchiveNamingTests.cs:120`.
On the authorized `:321`-revert control the manifest-reported name assertion
fires before the on-disk assertions, so the on-disk block never executes on that
path. Test-only; not a condition of acceptance, and not touched.

### The backup read side is `*.zip`

The read side enumerates backup archives by extension alone and was not changed
by A7. Existing `reefin-backup-*.zip` archives therefore stay listable and
restorable across the stem change. No archive is renamed on disk and no
migration exists, because none is needed.

### Everything else each slice retained

Each slice's remaining non-blocking findings and observations stand where its
review left them — in that slice's pull request body and document — unrepaired
by this closeout. This section enumerates only the four residuals the closeout
ruling names.

---

## 4. Status

W2-A0 through W2-A7 are each accepted on `master` at
`fb43b1627f1f77abf88777344670c33a7464ecbd`. This document records that surface
and nothing beyond it.

Independent review of this closeout is the next gate. #234 stays open.
