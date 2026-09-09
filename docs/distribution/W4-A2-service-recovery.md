# W4-A2 (#234): the SCM failure actions, matching W0 §4

W4-A0 built an MSI that registers the service `Tesserafin` and W4-A1 froze the
`UpgradeCode` that service's package is identified by. Neither authored what the
Service Control Manager should do when that service **dies**. This slice does,
and it proves exactly one thing:

> the installed service carries the `docs/distribution/W0-windows-server.md` §4
> recovery policy, read back out of the Service Control Manager after the
> install rather than out of the authoring.

**Design this implements:** `docs/distribution/W0-windows-server.md` §4, the
recovery row.

---

## 1. The policy

W0 §4's table states it in prose:

> restart after 60 s on first and second failure; no action on the third, so a
> crash loop is visible rather than hidden

In the SCM's own notation — the notation `sc.exe` uses, and the units the
`SC_ACTION` structure uses — that is:

    sc failure Tesserafin reset= 86400 actions= restart/60000/restart/60000//0

| Failure | Action | Delay |
| --- | --- | --- |
| first | restart the service | 60 s (`60000` ms) |
| second | restart the service | 60 s (`60000` ms) |
| third | **no action** | — |

`ResetPeriod` is `86400` seconds: a day without a failure puts the counter back
to zero, so the policy governs a crash loop rather than a service that has
crashed three times over its lifetime.

### Why the third entry is not decoration

The SCM repeats the **last** configured action for every failure past the end of
the array. An authoring that stopped after two restarts would therefore restart
the server for the third failure, and the fourth, and every one after that —
which is precisely the "crash loop hidden" outcome §4's third row exists to
refuse. The explicit `SC_ACTION_NONE` third entry is what makes the loop stop
and become visible in the Event Log and in the service's state.

That is why `serviceFailureActionCountIsContract` asserts **exactly three**
actions rather than "at least three", and why `third-action-restart` is a
hostile control of its own.

---

## 2. What this slice is not

* not the W0 §9 ACLs — this package still grants nothing;
* not signing, and no certificate is referenced;
* not a bit-identical MSI — W0 §5.6 measured that and accepted a bounded
  exception; nothing here builds the same package twice;
* not starting the service. W0 §10 leaves a fresh installation installed and
  enabled but not started, and `serviceNotStartedByInstall` still asserts
  `Stopped` after every install in this run;
* not a change to the `UpgradeCode`. Those 36 bytes are byte-for-byte what
  W4-A1 froze, and `findings_for_upgrade_code` still reddens any other value;
* not a claim that W4 is accepted, or that W3 is;
* not the rest of W0 §4's table. The **stop timeout** (120 s) and the **logging**
  rows are authored nowhere and measured nowhere, and this slice rules on
  neither. See §7.

---

## 3. The authoring

`packaging/windows/msi/Tesserafin.wxs`, on the **existing** `ServiceInstall` —
no second component, no custom action, no `WixToolset.Util.wixext` dependency:

```xml
<ServiceConfigFailureActions OnInstall="yes" OnReinstall="yes" ResetPeriod="86400">
  <Failure Action="restartService" Delay="60000" />
  <Failure Action="restartService" Delay="60000" />
  <Failure Action="none" Delay="0" />
</ServiceConfigFailureActions>
```

This is core WiX, not an extension: it emits a row in the
`MsiServiceConfigFailureActions` table, which the `MsiConfigureServices`
standard action applies inside the same install transaction that created the
service. WiX answers `WIX1149` for it — the same warning it already answers for
the `ServiceConfig` element that carries the delayed-autostart half of §4, and
for the same documented reason. That warning is exactly why nothing here is
graded from the authoring.

`OnUninstall` is deliberately absent: there is no service left to configure.

---

## 4. The readback

The measurement asks the Service Control Manager, through
`QueryServiceConfig2W(SERVICE_CONFIG_FAILURE_ACTIONS)`, after the install and
before the uninstall — while the service the installer created still exists.

Two alternatives were rejected and are recorded rather than graded:

| Source | Why not graded |
| --- | --- |
| the `FailureActions` `REG_BINARY` under the service key | Microsoft documents no layout for it; a hand-rolled parser would be the thing most likely to be wrong |
| `sc.exe qfailure Tesserafin` | its output is localised, so a text gate would grade the runner's language pack |

Both are captured verbatim in the evidence document — the `sc.exe` text and the
registry value as hex — so a reviewer can read the same policy in the two forms
they would reach for by hand, without the run depending on either.

`FailureActionsOnNonCrashFailures` is recorded read-only for the same reason it
is not set: W0 §4 is silent on it. It is the flag that decides whether a
**non-crash** exit counts as a failure at all, and W3's contract makes a fatal
startup failure exit non-zero rather than crash — so whether the recovery policy
fires for that case is a question W0 has to answer before it can be implemented.
This slice states the question and takes no position.

---

## 5. What is graded, and where

| Predicate (`ci/windows/w4/W4MsiAssertions.psm1`) | Green when |
| --- | --- |
| `serviceFailureActionsConfigured` | the SCM reports a failure policy at all |
| `serviceFailureResetPeriodIsContract` | `ResetPeriod` is `86400` seconds |
| `serviceFailureActionCountIsContract` | there are **exactly** three actions |
| `serviceFailureFirstIsRestartAfter60s` | the first is `restartService` after `60000` ms |
| `serviceFailureSecondIsRestartAfter60s` | the second is `restartService` after `60000` ms |
| `serviceFailureThirdIsNoAction` | the third is `none` |

Each compares **both** halves of an entry — the action and the delay. A gate
that compared only the action would call `restart after 1 s` correct, and a
one-second restart loop is the thing §4's 60 s exists to rule out; that is what
`first-action-not-restart` proves.

The static half is `ci/windows/w4/msi-controls.py`:

| Gate | Reads | Fires when |
| --- | --- | --- |
| `findings_for_failure_actions` | the authoring, comments stripped | no `ServiceInstall` carries the §4 policy, or not all of them do |
| `findings_for_failure_actions_prose` | `W4-A0-wix-skeleton.md`, this document | the W4-A0 document reclaims the whole §4 table, or this document stops stating the policy |

`findings_for_failure_actions` is a **presence** gate by construction. The
authoring carries deliberately broken recovery policies too — they are how the
hostile controls drive the real authoring rather than a copy of it written for
the test — so "no `Failure` with the wrong delay appears anywhere in this file"
is not a property this file can have. What it can have, and what is checked, is
that the number of correct policies equals the number of `ServiceInstall`
elements.

---

## 6. Hostile controls

Three new mutations build a deliberately broken MSI from the **same** authoring
and run the **same** grader, and each must redden **exactly** its declared set —
a control that reddens more has broken something else too and is attributable to
nothing, and one that reddens less has not reproduced its defect.

| Mutation | Declared RED set |
| --- | --- |
| `no-failure-actions` | all six recovery predicates |
| `first-action-not-restart` (still a restart, but after 1 s) | `serviceFailureFirstIsRestartAfter60s` |
| `third-action-restart` (the loop hidden) | `serviceFailureThirdIsNoAction` |

`no-failure-actions` declares six because a service with no policy has no reset
period, no count and no first, second or third entry: one defect with six
visible consequences, declared in full for the same reason `no-exe` declares
three. A declared set of only the first would pass while the grader quietly
stopped answering the other five.

The four W4-A0 controls are unchanged and still declare exactly what they
declared, which is the evidence that the recovery predicates did not start
reddening things they should not: each of the four leaves the recovery policy
correct, and each still reddens only its own set.

---

## 7. What a reviewer should check next

* the **stop timeout** row of W0 §4 — 120 s, "derived from the worst observed
  shutdown". Nothing in the package states it, so the SCM uses the machine-wide
  default, and a transcode-shutdown that outlasts it would be killed;
* the **logging** row — the Windows Event Log for service-lifecycle events. No
  Event Log source is registered by this package;
* `FailureActionsOnNonCrashFailures`, per §4 above: W3's non-zero exit on a
  fatal startup failure is a non-crash failure, and whether the §4 recovery
  policy should fire for it is a W0 question;
* whether a *deliberate* `sc stop` correctly does **not** count as a failure —
  it does not, by the SCM's own definition, but no run here has demonstrated it;
* the four retained-state component GUIDs, still open from W4-A1 §6.

---

## 8. Evidence

**Base.** Branched from `b279d76ac0e5079b16a6979a1e43bfde74b1531c`, the accepted
W4-A1 master named by the ruling, confirmed equal to `origin/master` before any
file was touched.

**Changed paths.** Exactly the five the ruling authorizes:
`packaging/windows/msi/Tesserafin.wxs`, `ci/windows/w4/W4MsiAssertions.psm1`,
`ci/windows/w4/assertion-self-test.ps1`, `ci/windows/w4/build-msi.ps1`,
`ci/windows/w4/msi-controls.py`, `ci/windows/w4/probe-msi-skeleton.ps1`, this
document, and the one sentence of `docs/distribution/W4-A0-wix-skeleton.md` §3
that the ruling names. `.github/workflows/w4-windows-msi.yml` is **not** among
them and is untouched — its `paths:` filter already watches
`packaging/windows/**` and `ci/windows/w4/**`, so the job fires on this branch
without being edited.

**The `UpgradeCode` did not move.**
`git diff b279d76ac0 -- packaging/windows/msi/Tesserafin.wxs | grep -c UpgradeCode`
is `0`: no diff line in the authoring mentions the attribute at all. The file
still carries exactly one `UpgradeCode` attribute and its value is still
`0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05`, ordinal, lowercase, unbraced. The
W4-A1 freeze controls are unchanged and green (below).

**The eight preprocessor branches were compiled, not reasoned about.** WiX
`6.0.2+b3f3403` — the pinned toolset — was run over the authoring once per
mutation against a synthetic stage. `wix` refuses to produce an MSI on Linux
(`WIX0000: The WiX Toolset only supports Windows`) and answers eight `WIX0389`
path errors on this host, but it parses and compiles the whole document first,
and it answers `WIX1149` per authored `ServiceConfigFailureActions`. The base
authoring answers the **same eight** `WIX0389` errors, so this slice adds no
compile error, and the `WIX1149` line number says which branch each mutation
selected:

| Mutation | Recovery policy authored at |
| --- | --- |
| `none` | line 281 — the real policy |
| `no-exe` | line 281 |
| `no-service-flag` | line 200 |
| `no-path-flags` | line 219 |
| `no-service-remove` | line 281 |
| `no-failure-actions` | **none authored** |
| `first-action-not-restart` | line 265 |
| `third-action-restart` | line 275 |

`no-failure-actions` emitting no `WIX1149` for `ServiceConfigFailureActions` at
all is the compile-time half of that control: the package genuinely carries no
recovery policy rather than a differently-shaped one.

**The `QueryServiceConfig2W` interop compiles.** The `Add-Type` block in the
probe was compiled on its own under PowerShell 7.6.5 and the `W4Scm` type
loaded. It cannot be *called* off Windows; what this establishes is that a
syntax error in it would not first surface two hours into a hosted run.

**The grader is not inert.** `ci/windows/w4/assertion-self-test.ps1`, on
synthetic observations, over 8 mutations and 30 predicates:

    OK   none                     every predicate green
    OK   no-exe                   msiContainsServerExe, installedServerExe, serviceImagePathIsInstalledExe
    OK   no-service-flag          serviceImagePathHasServiceFlag
    OK   no-path-flags            serviceImagePathHasWebDir, serviceImagePathHasFfmpeg
    OK   no-service-remove        uninstallRemovedService
    OK   no-failure-actions       serviceFailureActionsConfigured, serviceFailureResetPeriodIsContract,
                                  serviceFailureActionCountIsContract, serviceFailureFirstIsRestartAfter60s,
                                  serviceFailureSecondIsRestartAfter60s, serviceFailureThirdIsNoAction
    OK   first-action-not-restart serviceFailureFirstIsRestartAfter60s
    OK   third-action-restart     serviceFailureThirdIsNoAction

Each reddened **exactly** its declared set, all eight red sets are distinct —
two controls with identical failure lists is the tell that a harness grades
nothing — and the four W4-A0 controls still declare exactly what they declared
before this slice, which is the evidence that the six new predicates did not
start reddening anything they should not.

**The static controls.** `python3 ci/windows/w4/msi-controls.py --self-test`,
exit 0:

    W4-A0 / W4-A2 static controls
      no findings
      5 controls, all RED as declared                      (workflow surface, W4-A0)
      7 UpgradeCode freeze controls, all RED as declared   (W4-A1, unchanged)
      7 recovery controls, all RED as declared             (W4-A2)
    W4 static controls: clean

The seven recovery controls are: no failure actions authored; the first failure
is not `restart/60000`; the third failure is a restart; the reset period is not
`86400`; one `ServiceInstall` loses its recovery row; the W4-A0 document
reclaims the whole §4 table; this document drops the policy. Each was written
to the text and required to be caught, and each was.

Every recovery mutation replaces **every** occurrence rather than the first.
The authoring carries deliberately broken policies of its own, so a
first-occurrence replace would land on a control branch, leave the real policy
intact, and the gate would correctly stay green while the self-test claimed to
have tripped it.

**Secret scan.** `ci/secret-scan.sh --mode tree` — `CLEAN: the current tree
contains no findings`, exit 0, on a worktree with no build output present.

**Hosted run.** Pending. The stop condition for this slice is the hosted
`W4 Windows MSI` job reading the live failure policy off the installed service;
the measurement is recorded here in a follow-up commit on this branch, and the
evidence document the job prints carries `failureActionsObserved` for the real
package plus the `sc.exe qfailure` text and the `FailureActions` registry bytes
for all eight runs.
