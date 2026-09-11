# W4-A5 — remember `INSTALLFOLDER` across a MajorUpgrade

Tracker: [#234](https://github.com/tesserafin-project/tesserafin/issues/234).
Ruling: **W4-A5 REMEMBER INSTALLFOLDER**, authorising this slice from master at
`397baace26fd172fd0bac971144b0b5ad1a4b3de`.

W4-A4 §5 recorded a finding rather than fixing it, and the ruling quotes it back:

> the authoring has no remember-property, so an operator upgrade that omits
> `INSTALLFOLDER` relocates the binaries.

W4-A5 authors that property and measures the omission. It claims that and
nothing else.

## 1. What is claimed

Install package **A** with `INSTALLFOLDER=P`, a disposable prefix. Write a
sentinel into each of the four `%ProgramData%` state directories. Install
package **B** — same `UpgradeCode`, higher `Version` — with **no**
`INSTALLFOLDER=` on its `msiexec` command line at all. Then, read back off the
machine:

| Read back | Predicate |
| --- | --- |
| B's command line carried no `INSTALLFOLDER` | `upgradeOmittedInstallFolder` |
| `tesserafin.exe` is still under P | `installedServerExe` |
| …and so are `web\` and the FFmpeg runtime | `installedWebDir`, `installedFfmpegExe` |
| …and its bytes are the ones B was built from | `exeIsB`, `exeReplaced` |
| nothing reached `%ProgramFiles%\Tesserafin` | `defaultLocationUntouched` |
| the package still says where it lives | `rememberedPrefixIsInstallPrefix` |
| the four sentinels are still there, byte for byte | `stateSentinelsSurvivedUpgrade`, `stateSentinelContentsUnchanged` |
| the service is registered, Stopped, with the W0 §4 binPath under P | `serviceRegisteredAfterUpgrade`, `serviceStoppedAfterUpgrade`, `serviceImagePath*` |

The three rows in the middle are new. The rest are W4-A4's, unchanged and
re-answered — this slice does not narrow what the upgrade pair already proved,
it removes the crutch the pair was leaning on.

Fifty predicates, answered by `Get-W4UpgradePredicates` in
`ci/windows/w4/W4MsiAssertions.psm1`, which is pure — an observation in, a
predicate map out — and which `ci/windows/w4/assertion-self-test.ps1` drives on
any host, in under a second, with no MSI, before the hosted job spends any
runner time.

The frozen `UpgradeCode` is

```
0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05
```

exactly where W4-A1 froze it. This slice does not touch it, and does not touch
the W0 §9.3 SDDL or the W0 §4 argument list either.

## 2. The property, and the shape that is wrong

WiX 4 has no `RememberProperty` element. What it has is three pieces, and the
authoring carries all three:

| Piece | What it is |
| --- | --- |
| `Component` `RememberedInstallFolder` | key path is the value itself: `HKLM\Software\Tesserafin\Server` `InstallFolder` = `[INSTALLFOLDER]` |
| `Property` `REMEMBEREDINSTALLFOLDER` | a `RegistrySearch` that reads it back through AppSearch, `Type="raw"` |
| `SetProperty` | copies it into `INSTALLFOLDER`, `After="AppSearch"`, conditioned on `NOT INSTALLFOLDER` |

The shorter form — hanging the `RegistrySearch` directly on
`Property Id="INSTALLFOLDER"` — is wrong, and it is wrong in a way **no hosted
pair in this slice can see**. AppSearch *overwrites* the property it searches
for, including one the operator passed on the command line, so that form pins
the first prefix forever: an operator who wanted to move the installation could
not. Both halves of a hosted pair use one prefix, so the short form would grade
green here and fail an operator later. `ci/windows/w4/msi-controls.py` therefore
refuses a `Property` element that declares `INSTALLFOLDER`, and refuses a
`SetProperty` that is unconditioned or scheduled anywhere but after AppSearch.

Command-line precedence is **not** proven by this slice. No pair here relocates
an installation, so the condition is enforced statically and measured nowhere.
That is a smaller claim than the gate, and saying so is better than a claim no
run backs.

Two further choices are deliberate:

* `Type="raw"`, not `Type="directory"`. The stored value is the formatted
  `[INSTALLFOLDER]`, which carries Windows Installer's trailing separator, and a
  `directory` search additionally requires the path to exist when AppSearch
  runs — which is exactly when it may not;
* the component is neither `Permanent` nor `NeverOverwrite`, and both would be
  defects here rather than caution. `Permanent` would leave the value behind
  after an uninstall, so a later *fresh* install would silently resurrect a
  prefix the operator had removed. `NeverOverwrite` is skipped when its key path
  already exists, so an install that **was** given a new prefix would keep
  advertising the old one.

## 3. What the two packages are, and what changed in the probe

A and B are what W4-A4 made them: one accepted W2 package, one builder, this
authoring, differing only in the `-PatchBump` that raises B's PATCH field and in
the marker appended to A's `tesserafin.exe`. `SharedVersion.cs` is not edited,
on this branch or anywhere.

One line of `ci/windows/w4/probe-msi-upgrade.ps1` changed meaning: every B is
now installed as `msiexec /i B.msi` with no property assignment. A is still
installed with `INSTALLFOLDER=P` — it is what puts the product under a
disposable prefix in the first place — and so is the record-only downgrade.
`msi-controls.py` gates that per call rather than by a count: a B that is told
the prefix again is measuring the W4-A4 sequence, and an A that is not told it
never leaves the runner's real `%ProgramFiles%`.

### The prefix marker

One file is written into P between the two installs, beside the four state
sentinels and for a related reason. Windows Installer removes a directory it
created once the last file leaves it, so in the relocating control A's removal
would take P with it — and a P the probe cannot read makes `Get-W4Acl` answer
`$null`, which reddens all five W0 §9.3 `INSTALLFOLDER` rows. That would give
**one** defect five more consequences that are about the grader's reach rather
than about the defect. The marker keeps P readable in every pair, so those rows
say the same thing in all of them. It is never graded, and the pair's own
cleanup removes it with the prefix.

The consequence is stated rather than hidden: in the `upgrade-no-remember` pair
the descriptor read at P is **A's**, because B never installed there. Those five
rows are therefore not in that control's declared set, and the probe records
`installPrefixExistsAfterB` so a reviewer can see which package's work was read.

## 4. The hostile control

One, live, and it is the ruling's first:

| Control | The defect | Declared RED |
| --- | --- | --- |
| `upgrade-no-remember` | B carries none of the three elements above, so an upgrade whose command line says nothing resolves the default directory | nine rows |

The nine:

```
installedServerExe  installedWebDir  installedFfmpegExe  exeIsB
serviceImagePathIsInstalledExe  serviceImagePathHasWebDir  serviceImagePathHasFfmpeg
defaultLocationUntouched  rememberedPrefixIsInstallPrefix
```

One defect, nine visible consequences, declared in full for the reason
`upgrade-no-service` declares nineteen: three are the layout that is no longer
under P, one is the digest that is not B's there, three are the service binPath
now naming the default tree, and two are this slice's own rows.

Three rows stay **green** and must:

* `exeReplaced` asks whether what is under P is still A's bytes. Nothing is
  under P at all, so it is not A's. A control that reddened it would be
  indistinguishable from one that delivered a third executable;
* the five `installFolder*` §9.3 rows, for the marker reason in §3;
* `upgradeOmittedInstallFolder`. This control omits the property exactly as the
  real pair does — that is the point of it. The defect is in the package, not in
  the command line.

The control is authored in `Tesserafin.wxs` as one more `Mutation` value, so it
drives the real authoring rather than a copy written for the test. It is the
authoring as W4-A4 left it, which is precisely what makes it the NB-3 defect.

## 5. Static controls

`ci/windows/w4/msi-controls.py --self-test` runs before any runner time is
spent, and gained twelve controls of its own, each a mutation that must make a
gate fire:

| Mutated | Gate |
| --- | --- |
| nothing reads the remembered location | `findings_for_remember_property` |
| nothing writes it | ” |
| it never reaches `INSTALLFOLDER` | ” |
| the copy runs after the directory is resolved | ” |
| the copy overwrites the command line | ” |
| the search is authored on `INSTALLFOLDER` itself | ” |
| the upgrade is told the prefix again | `findings_for_omitted_install_folder` |
| no install upgrades to B | ” |
| the first install forgets the prefix | ” |
| this document drops the hostile control | `findings_for_remember_prose` |
| …claims the stage | ” |
| …drops the frozen `UpgradeCode` | ” |

"the copy overwrites the command line" and "the search is authored on
`INSTALLFOLDER` itself" are the two a hosted pair cannot reach.

## 6. What is deliberately not claimed

The ruling's "not this slice" list, restated as things this slice does not do:

* **no Event Log source**, **no repair**, **no signing**;
* **the service is not started.** W0 §10 leaves it installed, enabled and
  stopped, and this slice leaves it there;
* **no `UpgradeCode`, SDDL or §4 change.** None of those bytes moves;
* **no claim about the acceptance of this stage.** Independent review is next;
* **no command-line-precedence claim**, per §2;
* no reproducibility claim: W0 §5.6 already measured that MSI bytes are not
  bit-for-bit and accepted a bounded exception.

## 7. Findings recorded rather than fixed

**The W4-A4 document's §5 is now stale.** It says the probe "passes
`INSTALLFOLDER` explicitly to **both** installs" and that "a static control
refuses a probe that stops doing so". Both sentences described the tree before
this slice; the static control has inverted for B. Editing that document is not
on this ruling's authorised path, so the correction is recorded here instead of
made there.

**The workflow's `paths:` filter does not name this document.** It names
`docs/distribution/W4-A0-wix-skeleton.md` and no other W4 document — a gap W4-A1
through W4-A4 each left in turn, and which this slice matches rather than
changes, the ruling permitting the workflow to be edited only to extend the
upgrade job. A change to this file alone therefore does not run the controls
that read it; `ci/windows/w4/**` and `packaging/windows/**` cover everything
else.

**The probe's refusal messages still say `W4-A4`.** They are pre-existing
strings on a shared probe that now proves two slices, and rewriting them would
touch lines this slice has no reason to touch. The evidence document's `slice`
field says `W4-A4 + W4-A5`.

## 8. Where it runs

`.github/workflows/w4-windows-msi.yml`, job `msi-upgrade`, on a native
`windows-latest` runner — the same job W4-A4 added, extended to seven A→B pairs
and a path table. No `permissions:` change: `contents: read` and
`packages: read`, the second needed only because the frozen assembler pulls the
accepted Web payload image with the job's own token.

## 9. Authored surface

| Path | Change |
| --- | --- |
| `packaging/windows/msi/Tesserafin.wxs` | the three remember elements, one `Mutation` branch, the W4-A5 header |
| `ci/windows/w4/build-msi.ps1` | the `upgrade-no-remember` mutation name |
| `ci/windows/w4/W4MsiAssertions.psm1` | three predicates and the control's declared set |
| `ci/windows/w4/probe-msi-upgrade.ps1` | B installs without `INSTALLFOLDER`; the marker, the seventh pair, the path table |
| `ci/windows/w4/assertion-self-test.ps1` | the new control and two blind-spot assertions |
| `ci/windows/w4/msi-controls.py` | the inverted install gate, the remember-property gate, this document's gate, and their self-tests |
| `.github/workflows/w4-windows-msi.yml` | the upgrade job's step name and report line |
| `docs/distribution/W4-A5-remember-installfolder.md` | this file |
