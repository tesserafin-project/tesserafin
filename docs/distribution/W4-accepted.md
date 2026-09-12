# W4 — the accepted `win-x64` MSI surface

Tracker: [#234](https://github.com/tesserafin-project/tesserafin/issues/234).
Ruling: **OWNER RULING — W4-CLOSEOUT**, 2026-09-12, authorising this document
from master at `295c5f2792ff2422e082b386b8c138674029d0b7`.

Accepted master at this closeout:
`295c5f2792ff2422e082b386b8c138674029d0b7`, which is W4-A6 as accepted (#274).
Frozen W4 starting master: `fd8187f15ed7aa99552971305d5ac3f414c4d958`, which is
W3-A1 as accepted (#267).

This document ships no behaviour. It records which slices W4 landed, at which
commits, what each of them proved in its own words, and what W4 did **not**
prove. Every claim here is already made, and already reviewed, in the slice
document and pull request the table names. Nothing new is claimed, and no
earlier document is amended.

---

## 1. The seven slices, with SHAs

Each row is one accepted slice. `Base` is `origin/master` immediately before the
landing; `Accepted head` is the landing SHA, which is also the SHA the
independent review read and the SHA the required checks ran at. Every landing
was a true fast-forward: `git merge --ff-only` then `git push origin master`,
with the `gh pr merge --rebase` fallback used by none of them, so each pull
request records its own accepted head as its merge commit and no SHA was
rewritten.

The SHAs below are recomputed from the object graph — `git rev-list`,
`git merge-base`, and each pull request's recorded merge commit compared against
its own head — not copied from any pull request body.

| Slice | What it proved | Base | Accepted head | PR | Commits | Slice document |
| --- | --- | --- | --- | --- | --- | --- |
| **A0** | WiX MSI skeleton installs the accepted `win-x64` layout and registers, then removes, the service | `fd8187f15ed7aa99552971305d5ac3f414c4d958` | `051699200423c0b055ba9599e62ab8041bdcfd3e` | [#268](https://github.com/tesserafin-project/tesserafin/pull/268) | 4 | [`W4-A0-wix-skeleton.md`](W4-A0-wix-skeleton.md) |
| **A1** | the 1.1 MSI `UpgradeCode` is frozen to one string, mechanically | `051699200423c0b055ba9599e62ab8041bdcfd3e` | `b279d76ac0e5079b16a6979a1e43bfde74b1531c` | [#269](https://github.com/tesserafin-project/tesserafin/pull/269) | 2 | [`W4-A1-upgradecode.md`](W4-A1-upgradecode.md) |
| **A2** | the installed service carries the W0 §4 recovery policy, read back out of the SCM | `b279d76ac0e5079b16a6979a1e43bfde74b1531c` | `fc31d06a093e3c4f072a29be124b7c7edf1848d5` | [#270](https://github.com/tesserafin-project/tesserafin/pull/270) | 4 | [`W4-A2-service-recovery.md`](W4-A2-service-recovery.md) |
| **A3** | the live ACLs after install match W0 §9.3, by SID and access mask, with inheritance broken | `fc31d06a093e3c4f072a29be124b7c7edf1848d5` | `779e9fec23e285cc3a1ba4aeede8d24835b3ce45` | [#271](https://github.com/tesserafin-project/tesserafin/pull/271) | 5 | [`W4-A3-programdata-acls.md`](W4-A3-programdata-acls.md) |
| **A4** | a real `MajorUpgrade` replaces the binaries and keeps the state | `779e9fec23e285cc3a1ba4aeede8d24835b3ce45` | `397baace26fd172fd0bac971144b0b5ad1a4b3de` | [#272](https://github.com/tesserafin-project/tesserafin/pull/272) | 8 | [`W4-A4-major-upgrade.md`](W4-A4-major-upgrade.md) |
| **A5** | `INSTALLFOLDER` is remembered across a `MajorUpgrade`, and the omission is measured | `397baace26fd172fd0bac971144b0b5ad1a4b3de` | `9f351f594d7abfb0bb4420acea16a96698afc48f` | [#273](https://github.com/tesserafin-project/tesserafin/pull/273) | 2 | [`W4-A5-remember-installfolder.md`](W4-A5-remember-installfolder.md) |
| **A6** | the installer creates the Windows Event Log source for service lifecycle, and uninstall removes it | `9f351f594d7abfb0bb4420acea16a96698afc48f` | `295c5f2792ff2422e082b386b8c138674029d0b7` | [#274](https://github.com/tesserafin-project/tesserafin/pull/274) | 2 | [`W4-A6-eventlog-source.md`](W4-A6-eventlog-source.md) |

Twenty-seven commits, zero merge commits: `git rev-list --merges --count
fd8187f15ed7aa99552971305d5ac3f414c4d958..295c5f2792ff2422e082b386b8c138674029d0b7`
is `0` and `git rev-list --count` over the same range is `27`, the sum of the
seven `Commits` cells. Every base and accepted head above satisfies
`git merge-base --is-ancestor <sha> 295c5f2792ff2422e082b386b8c138674029d0b7`.
The chain is continuous — for each slice, `git merge-base <base> <head>` is the
base itself, and `git rev-list --count <base>..<head>` equals that slice's
commit count, so no unrelated commit sits between one slice's accepted head and
the next slice's base.

Each pull request's recorded merge commit is its own accepted head, which is
what a fast-forward landing looks like in the graph: #268 `051699200423…`, #269
`b279d76ac0…`, #270 `fc31d06a09…`, #271 `779e9fec23…`, #272 `397baace26…`, #273
`9f351f594d…`, #274 `295c5f2792…`.

Each slice was accepted individually, by its own `OWNER RULING — W4-A<n>-READY-FF`
on #234, after an independent hostile review verdict of ACCEPT with zero
blocking findings. This document is the union of those seven acceptances and
adds no eighth.

---

## 2. What W4 does not claim

The following stay out. None of them is proved by any accepted slice, and
naming them here neither opens nor schedules them.

* **A bit-identical MSI.** No slice built the package twice and compared bytes.
  W4 makes no reproducibility claim about the MSI itself; the two-clean-build
  proof W0 requires is the FFmpeg runtime's, not the installer's.
* **Signing and Authenticode.** No certificate, no key, no timestamp, no signed
  artifact. The explicit release-signing decision W0 requires is unmade.
* **Advertised repair.** The lifecycle W4 measured is install, upgrade and
  uninstall. Repair semantics are unexercised.
* **Application and media Event Log events.** A6 registered the source and
  measured service-lifecycle events through it. No application event and no
  media event is written or read by any slice.
* **`win-arm64`.** Nothing here is built, installed or measured for
  `win-arm64`, and no release promise is made for it.

---

## 3. Residuals — named, not repaired

These are carried forward exactly as the accepting rulings left them. None is a
condition of any acceptance already granted, and recording them here neither
repairs them nor authorizes a repair. This section enumerates only the
residuals the closeout ruling names.

### A3 — the inert workflow guard and three unreddenable predicates

**NB3** — the one-line workflow guard is inert. `appliedAcls` is the hardcoded
literal `$true` at `probe-msi-skeleton.ps1:156`, so
`if (-not $evidence.appliedAcls) { throw … }` can never fire, and reverting the
line on a disposable copy left `msi-controls.py` clean — the guard is not
statically covered. It is not load-bearing and not a false-green hazard: the
ACLs are graded by eleven `Get-Acl` predicates, and `msi-controls.py:344`
independently requires a `PermissionEx` element to exist.

**O1** — three predicates no declared control reddens:
`installFolderServiceCanReadAndExecute`, `installFolderUsersHaveNoWrite` and
`dataRootInheritanceBroken`. Only the third is disclosed in-tree. All three are
falsifiable at the predicate level and the review reddened each of them with its
own plants, so they are load-bearing — just not mutant-covered.

### A4 — no remember-property, **closed by A5**

**NB-3** on #272 was that the authoring had no remember-property on
`INSTALLFOLDER`: the probe passed it explicitly to both installs and a static
control refused a probe that stopped doing so. This residual is **closed** by
W4-A5 (#273), which authored the property — `SetProperty` conditioned on
`REMEMBEREDINSTALLFOLDER AND NOT INSTALLFOLDER` — and measured the omission on
a real upgrade pair whose B install carried nothing on its command line. It is
listed here because the closeout ruling names it, and it is listed as closed.

### A5 — `upgradeOmittedInstallFolder` is a literal

`upgradeOmittedInstallFolder` is a runtime constant, a literal `$false`, while
the `psm1` comment and §1 of `W4-A5-remember-installfolder.md` both describe it
as a measurement. The fact it stands for is proved elsewhere — by the static
gate and by the nine declared rows of the `upgrade-no-remember` control — so the
misdescription did not block acceptance. It is unrepaired.

### A6 — the inert regex, the tautological log name, the unmeasured rethrow

* The `EventLog\\(?!Application\\)` static gate is **inert**: the backslashes
  are doubled, so the pattern cannot match the registry paths it is meant to
  reject. The A6 document and the #274 body overstate it as asserted and
  self-tested.
* `eventLogSourceLogIsApplication` is **tautological**: the instrument returns
  the log name it was passed, so the predicate cannot fail.
* The reader's **rethrow arm is unmeasured**. The R1 fix makes an unregistered
  provider read as "no events" rather than a throw; the arm that rethrows any
  other failure is not exercised by any control.

### `INSTALLFOLDER` remember is A5's work, not a residual

To be unambiguous, because §3 above mentions it twice: remembering
`INSTALLFOLDER` across a `MajorUpgrade` is **delivered and accepted** work —
W4-A5, #273, accepted head `9f351f594d7abfb0bb4420acea16a96698afc48f`. It is
not a residual of W4. The only A5 residual named here is the
`upgradeOmittedInstallFolder` literal.

### Everything else each slice retained

Each slice's remaining non-blocking findings and observations stand where its
review left them — in that slice's pull request body and document — unrepaired
by this closeout.

---

## 4. Status

W4-A0 through W4-A6 are each accepted on `master` at
`295c5f2792ff2422e082b386b8c138674029d0b7`.

> W4 accepted as the win-x64 MSI surface. W5 (signing) is not opened.

— `OWNER RULING — W4-CLOSEOUT`, 2026-09-12.

This document records that surface and nothing beyond it. Independent review of
this closeout is the next gate. #234 stays open.
