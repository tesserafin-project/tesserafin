# W4-A4 — MajorUpgrade replaces the binaries and keeps the state

Tracker: [#234](https://github.com/tesserafin-project/tesserafin/issues/234).
Ruling: **W4-A4 MAJOR UPGRADE**, authorising this slice from master at
`779e9fec23e285cc3a1ba4aeede8d24835b3ce45`.

W4-A4 adds no product behaviour. `MajorUpgrade` has stood in
`packaging/windows/msi/Tesserafin.wxs` since W4-A0 and no slice has ever
exercised it. This one does, and claims that and nothing else.

## 1. What is claimed

Install package **A**. Write a sentinel into each of the four `%ProgramData%`
state directories. Install package **B**, built from the same authoring with the
same `UpgradeCode` and a higher `Version`. Then, read back off the machine:

| Read back | Predicate |
| --- | --- |
| the executable under `INSTALLFOLDER` is no longer A's | `exeReplaced` |
| …and is the one B was built from | `exeIsB` |
| the four state directories are still there | `stateDirectoriesSurvivedUpgrade` |
| the four sentinels are still there | `stateSentinelsSurvivedUpgrade` |
| …byte for byte | `stateSentinelContentsUnchanged` |
| the service `Tesserafin` is still registered | `serviceRegisteredAfterUpgrade` |
| …and is Stopped | `serviceStoppedAfterUpgrade` |
| …with the W0 §4 binPath | `serviceImagePathIsInstalledExe`, `…HasServiceFlag`, `…HasConfigDir`, `…HasDataDir`, `…HasCacheDir`, `…HasLogDir`, `…HasWebDir`, `…HasFfmpeg` |
| …the W0 §4 start type and account | `serviceStartIsAutomatic`, `serviceStartIsDelayed`, `serviceAccountIsVirtualAccount` |
| …and the W0 §4 recovery row | `serviceFailure*` (six) |
| the W0 §9.3 descriptors still hold | `installFolder*`, `dataRootInheritanceBroken`, `stateDirectories*`, `serverDirectoryUsersHaveNoWrite` (eleven) |
| the `UpgradeCode` bytes did not move | `upgradeCodeIsFrozenInA`, `upgradeCodeIsFrozenInB`, `upgradeCodeStable` |
| A was superseded rather than joined | `previousProductRemoved`, `upgradedProductInstalled`, `productCodesDiffer`, `versionBIsHigher` |
| the upgrade itself succeeded | `upgradeInstallSucceeded` |
| B never asked to start the service | `bDoesNotStartService` |

Forty-seven predicates. They are answered by
`Get-W4UpgradePredicates` in `ci/windows/w4/W4MsiAssertions.psm1`, which is
pure — an observation in, a predicate map out — and which
`ci/windows/w4/assertion-self-test.ps1` drives on any host, in under a second,
with no MSI, before the hosted job spends any runner time.

The frozen `UpgradeCode` is

```
0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05
```

and W4-A4 reads it back out of **both built packages'** `Property` tables rather
than out of the authoring. `ci/windows/w4/msi-controls.py` is the gate on the
authoring; a `Property` row is what a machine actually matches an installed
product against, and the two are different statements.

## 2. What the two packages are

A and B are built from **one** accepted W2 package, through **one** builder,
from **this** authoring. They differ in exactly two ways.

**The version.** `ci/windows/w4/build-msi.ps1` gained one parameter,
`-PatchBump`, which adds 0 or 1 to the PATCH field of the version it already
reads out of `SharedVersion.cs`. It is a *bump* and not a version deliberately:
a `-Version` parameter would be exactly what the frozen assembler's own control
(`zip-controls.py` Z11) and `msi-controls.py`'s `FORBIDDEN_BUILDER_PARAMETERS`
both refuse — the identity of what is packaged supplied at call time instead of
travelling with the commit. MAJOR, MINOR and PATCH all still come from the
commit. The only thing a caller can say is "and one more than that".

`SharedVersion.cs` is **not** edited, on this branch or anywhere. The declared
version is `1.0.0`, so A is `1.0.0` and B is `1.0.1`.

**The executable.** A's `tesserafin.exe` carries a marker and B's does not.

This is what makes "the binaries are B's" a measurable statement at all. Both
packages come from one accepted stage, so without a difference their executables
would be the same bytes and the predicate would be satisfied by an upgrade that
delivered nothing.

**A** is the marked one, not B. That direction matters: what is left on the
machine at the end of the real pair is the **accepted package's own bytes**, and
the reading is "the marker is gone and the accepted executable is there" rather
than "a fixture is there". B is the accepted package with a bumped version and
nothing else.

The marker is appended to a copy of the accepted executable. The file stays a
well-formed PE — headers, sections and entry point are the accepted ones and
none of them moves. Nothing in this slice runs it: W0 §10 leaves a fresh
installation stopped and the W4-A4 ruling is explicit that starting the service
is not this slice. What is measured about the file is its digest.

## 3. The five hostile controls

Three are **live pairs** — a real A, a real deliberately broken B, a real
`msiexec` upgrade and a real read-back:

| Control | The defect | Declared RED |
| --- | --- | --- |
| `upgrade-same-exe` | B is built from the stage A was built from, so the upgrade redelivers A's bytes | `exeReplaced` |
| `upgrade-wipes-state` | B empties the four state directories on install | `stateSentinelsSurvivedUpgrade`, `stateSentinelContentsUnchanged` |
| `upgrade-no-service` | B registers no service at all | nineteen rows: the registration and everything that follows from it |

`upgrade-same-exe` reddens **one** row and not two. `exeIsB` stays green and must:
B's staged executable *is* that file. A control that reddened both would be
indistinguishable from a package that delivered no executable at all.

`upgrade-wipes-state` reddens the sentinels and not the directories. The four
retained-state components are still `Permanent`, so the directories survive; it
is the operator's *data* that goes.

Its mechanism is worth stating, because the two obvious ones are inert. Taking
`Permanent` off the retained components does nothing: MSI's `RemoveFolder`
removes only an **empty** directory, and a directory holding a sentinel is not
empty. Authoring the deletion *inside* those components does nothing either:
they are `NeverOverwrite`, and with `HKLM\SOFTWARE\Tesserafin` still present from
A's install the upgrade **skips** them, so anything inside them never runs. The
control is therefore `RemoveFile … On="install"` on the component that owns the
executable — a component every install of this package installs.

Two are **table controls**. The package is really built from this authoring and
its own tables are really read; one cell is changed in a **copy**, through
Windows Installer's own `View`/`Modify`; and it is deliberately never installed:

| Control | The defect | Declared RED |
| --- | --- | --- |
| `upgrade-upgradecode` | B's `Property`/`UpgradeCode` is a different GUID | `upgradeCodeIsFrozenInB`, `upgradeCodeStable` |
| `upgrade-starts-service` | B's `ServiceControl`/`Event` carries the start-on-install bit | `bDoesNotStartService` |

Neither is authored in `Tesserafin.wxs`, and that is not a workaround.
`msi-controls.py` reddens a second `UpgradeCode` string anywhere in that file and
reddens `Start="install"` anywhere in it — correctly, because either string in
the real authoring **would be the defect**. The control has to live somewhere
that is not the authoring, and the built package's own tables are the closest
place to the thing being graded.

Neither is installed, and that is the ruling's own reasoning rather than
convenience:

* a package carrying a different `UpgradeCode` does not upgrade A. It installs
  **beside** it — the second product the W4-A4 ruling forbids inventing — and
  then fights A for the service name;
* a package that starts the service inside the transaction is the 1920-to-1603
  rollback W0 §5.2 measured. The transaction would roll back, A would still be
  installed, the service would be Stopped, and the live reading would come back
  **green** for a package that asked for the forbidden thing.

Either way the control would be graded by an outcome that is not the defect.
Both are graded from the tables; the evidence document records
`tableDerived: true` and the reason; and the probe **refuses** if either edit did
not take, because a control that cannot fire is not a control.

The two table controls are graded against the real pair's own live observation
with only their own package's facts substituted. That observation is measured,
on the hosted runner, by the `none` pair that runs before them — the probe
refuses if it is not.

`bDoesNotStartService` is graded on **both** halves — the `ServiceControl` table
has no start-on-install bit **and** the live service is not Running — for the
rollback reason above. The table half reddens deterministically whatever the
runner did.

## 4. What is deliberately not claimed

The W4-A4 ruling's exclusions, restated as things this slice does not do, and
asserted as data in the evidence document:

* **no repair** and no advertised repair;
* **no downgrade claim.** The ruling permits recording it, so the real pair
  installs A over B **once**, at the end, and records the exit code. No predicate
  is graded on it;
* **no Event Log source**, **no signing**, **no started service**;
* **no second product**;
* **no claim about the acceptance of this stage.** Independent review is next.

`msi-controls.py` reddens a W4-A4 document that claims the stage, and it does so
by looking for the sentence rather than for the intent — so this document does
not contain that sentence even in order to disclaim it. That is the same
discipline the W4-A1 and W4-A2 prose gates already impose.

W4-A4 also makes no reproducibility claim: W0 §5.6 already measured that MSI
bytes are not bit-for-bit and accepted a bounded exception.

## 5. Findings recorded rather than fixed

**The authoring carries no remember-property.** `INSTALLFOLDER` is a public
property with no `RegistrySearch` behind it, so an operator upgrade run as a
bare `msiexec /i B.msi` — without repeating the `INSTALLFOLDER` the first
install used — would install the binaries to the package's default location
rather than to where A put them. The probe passes `INSTALLFOLDER` explicitly to
**both** installs, which is what makes the pair an upgrade of one installation;
a static control refuses a probe that stops doing so. Adding the remember-property
is a change to the authoring's install semantics, which this ruling does not
authorise. It is written down here for the slice that does.

**The §9.3 descriptors read after B are B's own work.** The six
`OperatorTreePermissions` components are neither `Permanent` nor
`NeverOverwrite` — deliberately, and the authoring says why — so the upgrade
re-applies every descriptor. Had they been `NeverOverwrite` like the four
retained-state components, the descriptors read after the upgrade would have
been the ones **A** applied, and grading them would have said nothing about B.
They are not, and it does.

**`ci/windows/w4/W4MsiInstruments.psm1` duplicates instruments that
`probe-msi-skeleton.ps1` carries inline.** That is a real duplication and it is
declared rather than left to be discovered. `probe-msi-skeleton.ps1` is the
accepted, hosted-measured W4-A0/A2/A3 proof; moving its instruments would put
eleven measured controls and three accepted rulings behind an untested edit in
order to save a file. Converging the two is a change a later slice can make
against **both** probes' green runs instead of against one. Nothing that decides
an outcome is duplicated: every predicate comes from `W4MsiAssertions.psm1`,
which W4-A4 extended rather than copied.

**The workflow's `paths:` filter does not name this document.** It names
`docs/distribution/W4-A0-wix-skeleton.md` and no other W4 document — a gap W4-A1,
W4-A2 and W4-A3 each left in turn, and which this slice matches rather than
changes, the ruling permitting the workflow to be edited only to add the A→B
pair and a report line. The consequence is that a change to this file alone does
not run the controls that read it. `ci/windows/w4/**` covers everything else.

## 6. Where it runs

`.github/workflows/w4-windows-msi.yml`, job `msi-upgrade`, on a native
`windows-latest` runner. It is a second job beside `msi-skeleton` and changes
nothing about it: the two are independent, neither consumes the other's output,
nothing is uploaded, and each assembles its own package from the pull request
head through the frozen W2-A2 assembler. Its `permissions:` block is byte for
byte the one `msi-skeleton` asks for — `contents: read` and `packages: read`,
the second needed only because the assembler pulls the accepted Web payload
image with the job's own token.

## 7. Authored surface

| Path | Change |
| --- | --- |
| `packaging/windows/msi/Tesserafin.wxs` | two `Mutation` branches and the W4-A4 header. No component added, no `UpgradeCode`, failure-action or SDDL value moved |
| `ci/windows/w4/build-msi.ps1` | `-PatchBump`, and the two mutation names |
| `ci/windows/w4/W4MsiAssertions.psm1` | `Get-W4UpgradePredicates`, `Get-W4UpgradeControlExpectations`, `Get-W4UpgradeVerdict` |
| `ci/windows/w4/W4MsiInstruments.psm1` | new — msiexec, the SCM, `Get-Acl` and the MSI tables |
| `ci/windows/w4/probe-msi-upgrade.ps1` | new — the six A→B pairs |
| `ci/windows/w4/assertion-self-test.ps1` | the upgrade grader's controls |
| `ci/windows/w4/msi-controls.py` | the W4-A4 static gates and their self-tests |
| `.github/workflows/w4-windows-msi.yml` | the `msi-upgrade` job and its report line |
| `docs/distribution/W4-A4-major-upgrade.md` | this file |
