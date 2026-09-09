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

**Amended by the W4-A2-R1 owner ruling on #234.** The policy in §1 is unchanged
and so is everything graded. What changed is the element that applies it: the
core `ServiceConfigFailureActions` made `MsiConfigureServices` answer MSI error
`1939` under `InstallFinalize` and roll the install back to `1603`, and the
ruling authorised `util:ServiceConfig` from `WixToolset.Util.wixext` in its
place — the first WiX extension this packaging depends on. See §3.1.

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
* not a `Directory.Packages.props` change. The ruling's STOP condition — "if
  that forces `Directory.Packages.props`" — was not reached, and §3.4 says why
  it structurally cannot be on this path;
* not the rest of `WixToolset.Util.wixext`. One element is used. No other
  extension element, and no other extension, is authored or referenced;
* not the rest of W0 §4's table. The **stop timeout** (120 s) and the **logging**
  rows are authored nowhere and measured nowhere, and this slice rules on
  neither. See §7.

---

## 3. The authoring

`packaging/windows/msi/Tesserafin.wxs`, on the **existing** `ServiceInstall` —
no second component, no authored custom action:

```xml
<util:ServiceConfig FirstFailureActionType="restart"
                    SecondFailureActionType="restart"
                    ThirdFailureActionType="none"
                    RestartServiceDelayInSeconds="60"
                    ResetPeriodInDays="1" />
```

with `xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util"` on `<Wix>`.

### 3.1 Why this is not the core element any more

This slice first authored the core `ServiceConfigFailureActions` element, which
emits a row in the `MsiServiceConfigFailureActions` table for the
`MsiConfigureServices` standard action to apply. **That package did not
install.** `msiexec` answered `1603`, and the verbose log — read only after the
W4-A2-DIAG2 excerpt was anchored at the end of it — named the cause:

    Error 1939. Service 'Tesserafin Server' (Tesserafin) could not be configured.

`1603` is "fatal error during installation", which is every rollback the engine
decides on; `1939` is the specific one, raised by `MsiConfigureServices` under
`InstallFinalize`. The owner ruling on #234 (**W4-A2-R1**) accepted that
diagnosis and authorised the replacement:

> I authorize replacing the core `ServiceConfigFailureActions` element with
> `WixToolset.Util.wixext` `util:ServiceConfig` on the existing
> `ServiceInstall`.

So `WIX1149` — which WiX answers for the core failure-actions element as well as
for the core `ServiceConfig` element, and which the first version of this
document recorded as a footnote — was not a footnote. WiX's own text for it says
what to do:

> ServiceConfig functionality is documented in the Windows Installer SDK to
> "not [work] as expected." Consider replacing ServiceConfig with the
> WixToolset.Util.wixext ServiceConfig element.

The delayed-autostart half of §4 is a **different** core element, `ServiceConfig`
with `DelayedAutoStart="yes"`. It has installed since W4-A0 and it stays; it
still answers `WIX1149`, and it is still read back out of the registry rather
than believed. Only the failure-actions half moved.

### 3.2 What the extension element actually does

`util:ServiceConfig` is not a table row. It schedules a deferred custom action
(`SchedServiceConfig` / `ExecServiceConfig`) that opens the service **after**
`InstallServices` created it and calls
`ChangeServiceConfig2W(SERVICE_CONFIG_FAILURE_ACTIONS)` directly, with the
`SERVICE_START` access right that `SC_ACTION_RESTART` requires — the extension's
own source says so in as many words:

    //  SERVICE_START is required in order to handle SC_ACTION_RESTART action.

That is the mechanism, and it is why the same policy can be applied where the
standard action refused it.

### 3.3 The units are the extension's, and the conversion is the CA's

`src/ext/Util/ca/serviceconfig.cpp`, `ConfigureService`:

```c
SC_ACTION actions[3]; // the UI always shows 3 actions, so we'll always do 3
...
actions[0].Type = GetSCActionType(wzFirstFailureActionType);
actions[0].Delay = 0;
if (SC_ACTION_RESTART == actions[0].Type)
{
    actions[0].Delay = dwRestartServiceDelayInSeconds * 1000; // seconds to milliseconds
}
...
sfa.dwResetPeriod = dwResetPeriodInDays * (24 * 60 * 60); // days to seconds
sfa.cActions = countof(actions);
```

Three consequences follow, and all three are the reason the authoring above is
the policy §1 states rather than merely resembling it:

* `RestartServiceDelayInSeconds="60"` becomes `60000` ms on each **restart**
  entry — §4's 60 s;
* `ResetPeriodInDays="1"` becomes `86400` s — §4's reset period;
* a non-restart entry keeps `Delay = 0`, and the array is **always** three
  entries long, so `ThirdFailureActionType="none"` is exactly the
  `SC_ACTION_NONE` with a zero delay that §1 requires.

There is **one** delay attribute and it feeds every restart entry. There is no
shape of this element in which only the first restart's delay is wrong, which is
why the delay control in §6 declares two predicates rather than one.

`OnUninstall` has no equivalent here and needs none: there is no service left to
configure.

### 3.4 The extension pin

`WixToolset.Util.wixext` is the **first** WiX extension this packaging depends
on. The ruling requires it pinned by exact version on the `wix` command line,
and `ci/windows/w4/build-msi.ps1` is the only file that states it:

```powershell
$utilExtension = "WixToolset.Util.wixext/$UtilExtensionVersion"   # 6.0.2
& wix extension add -g $utilExtension
# ... then `wix extension list -g` is READ BACK and the build is refused
#     unless the installed version is the one asked for ...
& wix build -arch x64 -ext $utilExtension ...
```

`Directory.Packages.props` is **not** touched, and the ruling's STOP condition —
"if that forces `Directory.Packages.props`" — was not reached. That is not luck
and not a preference: `wix` is a `dotnet tool`, this repository carries no
`.wixproj`, and nothing on the MSI path is a NuGet restore that central package
management could govern. An entry there would pin nothing while reading like the
pin, so `ci/windows/w4/msi-controls.py` reddens one being added, and reddens a
`.wixproj` appearing in the tree — which is the condition under which that
sentence would stop being true.

The version defaults to the toolset's own, `6.0.2`. The extension ships in
lockstep with the toolset, so one pin moving without the other is drift rather
than an upgrade.

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
`delay-not-60s` proves.

These six predicates are unchanged by W4-A2-R1. They were written against the
**SCM's** units and the **SCM's** readback, never against the authoring, so
moving the authoring from a table row to an extension element did not reach
them — which is the property that made the move measurable rather than a matter
of belief.

The static half is `ci/windows/w4/msi-controls.py`:

| Gate | Reads | Fires when |
| --- | --- | --- |
| `findings_for_failure_actions` | the authoring, comments stripped | no `ServiceInstall` carries the §4 policy, or not all of them do; **or the core `ServiceConfigFailureActions` element is still present**; or the `util` namespace is not declared |
| `findings_for_extension_pin` | `build-msi.ps1`, `Directory.Packages.props`, the tree | the extension is unnamed, unpinned, never `-ext`-ed, or never read back; or the pin migrated to `Directory.Packages.props`; or a `.wixproj` appeared |
| `findings_for_failure_actions_prose` | `W4-A0-wix-skeleton.md`, this document | the W4-A0 document reclaims the whole §4 table, or this document stops stating the policy |

The core-element gate is an **absence** gate, which is the kind most easily
written inert, so `--self-test` mutates the core element back **in** and
requires the gate to fire. It reads the authoring with XML comments stripped, so
this slice can go on explaining in prose which element it removed and why
without that prose being graded as the element itself.

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
| `no-util-config` (no `util:ServiceConfig` authored at all) | all six recovery predicates |
| `delay-not-60s` (still restarts, but after 1 s) | `serviceFailureFirstIsRestartAfter60s`, `serviceFailureSecondIsRestartAfter60s` |
| `third-action-restart` (the loop hidden) | `serviceFailureThirdIsNoAction` |

`no-util-config` declares six because a service with no policy has no reset
period, no count and no first, second or third entry: one defect with six
visible consequences, declared in full for the same reason `no-exe` declares
three. A declared set of only the first would pass while the grader quietly
stopped answering the other five.

Both W4-A2 mutation names changed under W4-A2-R1, and only because what they do
changed. `no-util-config` was `no-failure-actions`: it removed a table row, and
it now removes the extension element that schedules the custom action, which is
the ruling's own "util:ServiceConfig absent". Its declared set is unchanged —
the SCM ends up with no policy either way.

`delay-not-60s` declares **two**, and it did not before W4-A2-R1. It was
`first-action-not-restart` and it declared one, because the core element carried
a per-entry `Delay`. `util:ServiceConfig` carries one
`RestartServiceDelayInSeconds` for every restart entry, so a declared set of
only the first would be a set no package this repository can build could ever
produce, and the control would fail for being right. The rename is not
cosmetic: the ruling names this control "delay is not 60 s", and that is now
precisely what it is. The delay is still the half that changed — both entries
are still restarts, so a gate that asked only which **action** the SCM recorded
would still call this package correct.

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

**Changed paths.** Only paths the ruling authorizes:
`packaging/windows/msi/Tesserafin.wxs`, `ci/windows/w4/W4MsiAssertions.psm1`,
`ci/windows/w4/assertion-self-test.ps1`, `ci/windows/w4/build-msi.ps1`,
`ci/windows/w4/msi-controls.py`, `ci/windows/w4/probe-msi-skeleton.ps1`, this
document, and the one sentence of `docs/distribution/W4-A0-wix-skeleton.md` §3
that the ruling names. `.github/workflows/w4-windows-msi.yml` is **not** among
them and is untouched — the W4-A2-R1 ruling does not authorize it, its
`permissions:` block is one of the things the ruling forbids editing, and its
`paths:` filter already watches `packaging/windows/**` and `ci/windows/w4/**`,
so the job fires on this branch without being edited. `Directory.Packages.props`
is untouched.

**One sentence of `W4-A0-wix-skeleton.md` is now stale and was left alone.** Its
§8 "what a reviewer should check next" list says the delayed-autostart half
"needs `WixToolset.Util.wixext` — which would be the first extension dependency
this packaging takes on". The extension dependency is now taken, for the
failure-actions half rather than the delayed-autostart one. Only the NB-1
overclaim sentence of that document is authorized, so the stale sentence stands
and is named here instead.

**The `UpgradeCode` did not move.**
`git diff b279d76ac0 -- packaging/windows/msi/Tesserafin.wxs | grep -c UpgradeCode`
is `0`: no diff line in the authoring mentions the attribute at all. The file
still carries exactly one `UpgradeCode` attribute and its value is still
`0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05`, ordinal, lowercase, unbraced. The
W4-A1 freeze controls are unchanged and green (below).

**The extension resolves at the pinned version.**

    $ wix extension add  -g WixToolset.Util.wixext/6.0.2
    $ wix extension list -g
    WixToolset.Util.wixext 6.0.2

That is the line `build-msi.ps1` reads back and refuses on. Acquiring it needed
no `.wixproj`, no `Directory.Packages.props` entry and no NuGet restore of this
repository's own — the ruling's STOP condition was not reached.

The readback block was then run verbatim against that installed extension, and
run again asking for `6.0.1` — a version that exists on the feed but is not the
one installed:

    ACCEPTED  extension WixToolset.Util.wixext 6.0.2
    REFUSED as declared: asked for 6.0.1 and got WixToolset.Util.wixext 6.0.2

so the pin refuses a version it did not ask for rather than only appearing to.
`build-msi.ps1` cannot be run whole on this host — it resolves the accepted
layout with Windows separators, and `the stage has no 'ffmpeg\bin\ffmpeg.exe'`
is the refusal it reaches long before the toolset — which is why the block was
lifted out rather than the script driven end to end.

**The eight preprocessor branches were compiled, not reasoned about.** WiX
`6.0.2+b3f3403` — the pinned toolset, with the pinned extension on the command
line — was run over the authoring once per mutation against a synthetic stage.
`wix` refuses to produce an MSI on Linux (`WIX0000: The WiX Toolset only
supports Windows`) and answers eight `WIX0389` path errors on this host, but it
parses and compiles the whole document first. Every one of the eight branches
compiled with **eight** `WIX0389` and **zero** other errors, and the base
authoring at `e955b5a03d` answers the same eight for the same mutations — so
this slice adds no compile error, and `util:ServiceConfig` links.

The interesting number is `WIX1149`, per mutation, base against head:

| Mutation | base `WIX1149` | head `WIX1149` |
| --- | --- | --- |
| `none` | 2 — lines 236, 281 | **1** — line 250 |
| `no-exe` | 2 — lines 236, 281 | **1** — line 250 |
| `no-service-flag` | 2 — lines 199, 200 | **1** — line 213 |
| `no-path-flags` | 2 — lines 218, 219 | **1** — line 232 |
| `no-service-remove` | 2 — lines 236, 281 | **1** — line 250 |
| `no-util-config` (base: `no-failure-actions`) | 1 — line 236 | **1** — line 250 |
| `delay-not-60s` (base: `first-action-not-restart`) | 2 — lines 236, 265 | **1** — line 250 |
| `third-action-restart` | 2 — lines 236, 275 | **1** — line 250 |

The two renamed mutations were driven against the base under the **base's** own
names, not their new ones: a name the base's `<?elseif ?>` chain does not
contain falls through to the real policy, and the run would have compared the
head's control against the base's correct package.

The base answers **two** in every branch that authors a recovery policy: one for
the core `ServiceConfig` that carries delayed-autostart, one for the core
`ServiceConfigFailureActions` beside it. The head answers exactly **one** in
every branch, and it is always the delayed-autostart `ServiceConfig` — whose own
warning text is the instruction this slice followed:

    warning WIX1149: ServiceConfig functionality is documented in the Windows
    Installer SDK to "not [work] as expected." Consider replacing ServiceConfig
    with the WixToolset.Util.wixext ServiceConfig element.

That missing second `WIX1149`, in **all eight** branches rather than only in the
one the real package selects, is the compile-time proof that the core
failure-actions element is gone from the file — the hostile control the ruling
names as "core `ServiceConfigFailureActions` still present (must be gone)",
observed from the toolset rather than from a grep.

**What that costs, stated rather than hidden.** The base's second `WIX1149`
carried a line number, and that line number was how the base document showed
which recovery branch each mutation selected. There is no second `WIX1149` any
more, so the compile log no longer attributes the recovery branch: `none`,
`delay-not-60s` and `third-action-restart` are now indistinguishable in it. The
branch each mutation selects is established instead by
`ci/windows/w4/msi-controls.py --self-test` against the text and by the SCM
readback on the hosted runner, which is where it was always going to be decided.
No claim in this document rests on the compile log for it.

**The `QueryServiceConfig2W` interop compiles.** The `Add-Type` block in the
probe was compiled on its own under PowerShell 7.6.5 and the `W4Scm` type
loaded. It cannot be *called* off Windows; what this establishes is that a
syntax error in it would not first surface two hours into a hosted run.

**The grader is not inert.** `ci/windows/w4/assertion-self-test.ps1`, on
synthetic observations, over 8 mutations and 30 predicates:

    OK   none                 every predicate green
    OK   no-exe               msiContainsServerExe, installedServerExe, serviceImagePathIsInstalledExe
    OK   no-service-flag      serviceImagePathHasServiceFlag
    OK   no-path-flags        serviceImagePathHasWebDir, serviceImagePathHasFfmpeg
    OK   no-service-remove    uninstallRemovedService
    OK   no-util-config   serviceFailureActionsConfigured, serviceFailureResetPeriodIsContract,
                              serviceFailureActionCountIsContract, serviceFailureFirstIsRestartAfter60s,
                              serviceFailureSecondIsRestartAfter60s, serviceFailureThirdIsNoAction
    OK   delay-not-60s        serviceFailureFirstIsRestartAfter60s, serviceFailureSecondIsRestartAfter60s
    OK   third-action-restart serviceFailureThirdIsNoAction

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
      10 recovery controls, all RED as declared            (W4-A2, as amended by R1)
    W4 static controls: clean

The ten recovery controls are: no `util:ServiceConfig` authored; the `util`
namespace is not declared; **the core `ServiceConfigFailureActions` element is
back**; the restart delay is not 60 s; the third failure is a restart; the reset
period is not one day; the second failure is not a restart; one `ServiceInstall`
loses its recovery row; the W4-A0 document reclaims the whole §4 table; this
document drops the policy. Each was written to the text and required to be
caught, and each was.

Three of those ten are new under W4-A2-R1, and the third is the one that
matters: an **absence** gate is the kind most easily written inert, so the core
element is mutated back into the authoring and the gate is required to fire.
Grading the comment-stripped text is what lets §3.1 above go on naming the
element it removed without being reddened for saying its name.

Every recovery mutation replaces **every** occurrence rather than the first,
with one deliberate exception. The authoring carries deliberately broken
policies of its own, so a first-occurrence replace would land on a control
branch, leave the real policy intact, and the gate would correctly stay green
while the self-test claimed to have tripped it. The exception is "one
`ServiceInstall` loses its recovery row", where removing exactly one — the first
in the file, which is a correct one — is the whole point: it is the only way to
produce more `ServiceInstall` elements than policies, which is the drift the
count comparison exists to catch.

**Secret scan.** `ci/secret-scan.sh --mode tree` — `CLEAN: the current tree
contains no findings`, exit 0, on a worktree with no build output present.

**Hosted run.** Pending. The stop condition for this slice is the hosted
`W4 Windows MSI` job reading the live failure policy off the installed service;
the measurement is recorded here in a follow-up commit on this branch, and the
evidence document the job prints carries `failureActionsObserved` for the real
package plus the `sc.exe qfailure` text and the `FailureActions` registry bytes
for all eight runs.
