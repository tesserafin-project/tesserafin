# W4-A6 — the Windows Event Log source for service lifecycle

Tracker: [#234](https://github.com/tesserafin-project/tesserafin/issues/234).
Ruling: **W4-A6 EVENT LOG SOURCE**, authorising this slice from master at
`9f351f594d7abfb0bb4420acea16a96698afc48f`.

`docs/distribution/W0-windows-server.md` §4 asks for two logging sinks: the
application's own rolling file sink under the log directory, *plus* the Windows
Event Log **for service-lifecycle events only**. The file sink has existed since
the server did. The Event Log half has never worked on a machine this package
installed, and W3-A0 §3 recorded why and left it here:

> Under a non-administrator service identity an unregistered event source would
> also need the installer to create it — W4, together with §9's identity.

W4-A6 is the installer creating it. It claims that and nothing else.

## 1. What is claimed

Install the package. Then, **after** the transaction has finished, `sc start`
and `sc stop`. Then uninstall. Read back off the machine:

| Read back | Predicate |
| --- | --- |
| `msiexec /i` exited 0 | `installSucceeded` |
| an Event Log source named `Tesserafin` exists | `eventLogSourceRegistered` |
| …under the `Application` log | `eventLogSourceLogIsApplication` |
| …naming a message file this package ships | `eventMessageFileIsPackaged` |
| …and that file is actually on disk | `messageFileInstalled` |
| …with `TypesSupported` = 7 | `typesSupportedIsContract` |
| the **package** did not start the service | `packageDoesNotStartService`, `serviceStoppedAfterInstall` |
| the **probe's** `sc start` reached Running | `serviceReachedRunning` |
| the probe's `sc stop` reached Stopped, with no orphan | `serviceReachedStopped`, `noOrphanAfterStop` |
| at least one event was written under that source, in that window | `lifecycleEventUnderSource` |
| `msiexec /x` exited 0 | `uninstallSucceeded` |
| the source key is gone | `eventLogSourceRemoved` |
| …and `EventLog.SourceExists` agrees | `eventLogSourceGoneToTheApi` |

Fifteen predicates, answered by `Get-W4EventLogPredicates` in
`ci/windows/w4/W4MsiAssertions.psm1`, which is pure — an observation in, a
predicate map out — and which `ci/windows/w4/assertion-self-test.ps1` drives on
any platform, in under a second, with no MSI, before a runner is asked for.

## 2. What actually writes the events

Nothing in this repository does, and that is the point.

`System.ServiceProcess.ServiceBase` defaults `AutoLog` to **true** and writes one
entry when `OnStart` returns and one when `OnStop` does — the service-lifecycle
events §4 names. It writes them through an `EventLog` whose `Log` is
`Application` and whose `Source` is the `ServiceName`.
`Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime` derives
from `ServiceBase` and sets only `ServiceName` and `CanShutdown`, so the shell
W3-A0 put under the SCM has been trying to write these two events since W3-A0.

They have gone nowhere, silently, for a reason that is entirely about the
identity W0 §9.2 chose:

* `EventLog.WriteEntry` calls `VerifyAndCreateSource`, which **creates** a
  missing source before writing;
* creating one writes under
  `HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application`, which
  `NT SERVICE\Tesserafin` cannot do;
* `ServiceBase.WriteLogEntry` wraps the whole call in a `try`/`catch` that
  swallows everything.

So the service runs, the events are composed, the write fails, and nothing
anywhere says so. The installer is the only thing on the machine with the rights
to close it. `eventlog-no-source` below is that machine, measured.

## 3. The authoring

An Event Log source **is** a registry key. `EventCreate.exe`, .NET's
`EventLog.CreateEventSource` and WiX's own `util:EventSource` all write the same
one, so the source is authored with the **core** `RegistryKey` and
`RegistryValue` elements:

```xml
<Component Id="EventLogSourceRegistration" Guid="55ce15a7-640d-4216-b490-0236e5948fce">
  <RegistryKey Root="HKLM"
               Key="SYSTEM\CurrentControlSet\Services\EventLog\Application\Tesserafin"
               ForceDeleteOnUninstall="yes">
    <RegistryValue Name="EventMessageFile"
                   Value="[INSTALLFOLDER]System.Diagnostics.EventLog.Messages.dll"
                   Type="expandable"
                   KeyPath="yes" />
    <RegistryValue Name="TypesSupported" Value="7" Type="integer" />
  </RegistryKey>
</Component>
```

**No second WiX extension.** The ruling's "if this needs a WiX extension other
than Util 6.0.2, STOP" never came up: `WixToolset.Util.wixext` 6.0.2 does carry a
`util:EventSource`, and it would write these same two values through this same
registry table. The core elements state the contract directly and take no new
dependency, so no new dependency is taken.

**`ForceDeleteOnUninstall` is load-bearing, not tidiness.**
`EventLog.SourceExists` — and every other reader of the log — asks whether the
**subkey** exists and never looks at its values. A component that removed the two
values and left the key would leave `Tesserafin` registered as a source, and the
ruling's "uninstall removes the source" would be green while being false. So the
key itself is authored and the key itself is removed.

**The message file is the package's own.** `ServiceBase` writes with event id 0
and the message as the single insertion string, which renders only if the source
names a message file whose table has an entry for it. .NET's own
`CreateEventSource` points at the .NET Framework's `EventLogMessages.dll` and
falls back to `System.Diagnostics.EventLog.Messages.dll` beside the assembly.
That second file is a managed file of the `Microsoft.AspNetCore.App` win-x64
runtime pack, so it is already inside the self-contained publish the accepted W2
layout is — and naming it keeps a distribution whose whole premise is "needs no
system .NET runtime" from depending on the .NET Framework for the sake of a
string. Two predicates cover it rather than one: `eventMessageFileIsPackaged`
asks what the authoring claimed, `messageFileInstalled` asks whether the claim is
true on the machine, and a trimmed publish would separate them.

**The component is neither `Permanent` nor `NeverOverwrite`**, for the reason the
six `OperatorTreePermissions` components are neither: this key is package policy,
re-stated by every install and removed by the uninstall. It is not operator data.

**Nothing else in the authoring moved.** The frozen `UpgradeCode`
`0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05`, the three SDDL defines, the
`util:ServiceConfig` failure-action block and the W4-A5 remember-property are
byte for byte what W4-A5 left. `git diff 9f351f594d -- packaging/windows/msi/Tesserafin.wxs`
shows no line touching any of them.

## 4. The start is a probe, not the package

W0 §10 leaves a fresh installation *installed and enabled but not started*, and
that is still exactly what this package does. The start and the stop that produce
a lifecycle event are run by `ci/windows/w4/probe-msi-eventlog.ps1` after
`msiexec` has exited — the same shape as `ci/windows/w3/probe-service-host.ps1`.

Two predicates say so, and they are deliberately two:

* `serviceStoppedAfterInstall` reads the live SCM after the install;
* `packageDoesNotStartService` reads the built package's own `ServiceControl`
  table.

They fail differently. A package that asks for the start and whose start then
*fails* rolls the install back and leaves no service to observe at all, so the
live reading alone could be green on a package that had asked.

The probe does not stop the service the instant the SCM says Running, either. The
shell answers the SCM in milliseconds by design — that is the whole of W3-A0 —
and the real server is still applying migrations behind it. The probe waits for
the server's own readiness first, with W0 §2.3's four traps closed, so the stop
is a stop of a started server rather than a measurement of the half-started
shutdown path, which is W3's question and not this slice's.

## 5. The hostile controls

Three are authored as `Mutation` values in the real `Tesserafin.wxs` and built
through the real `ci/windows/w4/build-msi.ps1`, so each drives *this* authoring
rather than a copy of it. Each must redden **exactly** its declared set.

| Control | Shape | Declared RED |
| --- | --- | --- |
| `eventlog-no-source` | live | `eventLogSourceRegistered`, `eventLogSourceLogIsApplication`, `eventMessageFileIsPackaged`, `typesSupportedIsContract`, `lifecycleEventUnderSource` |
| `eventlog-source-survives` | live | `eventLogSourceRemoved`, `eventLogSourceGoneToTheApi` |
| `eventlog-start-install` | table | `packageDoesNotStartService` |

`eventlog-no-source` is the slice's whole argument. It installs, starts, stops
and uninstalls exactly like the real package; the service still reaches Running
and still reaches Stopped; and the lifecycle events go nowhere, because the
non-administrator identity cannot register the source `ServiceBase` needs.
`eventLogSourceRemoved` stays **green** and must: there was nothing to remove,
and a control that reddened it would be indistinguishable from one whose
uninstall failed.

`eventlog-source-survives` is one attribute, `Permanent="yes"`. Everything up to
the uninstall is byte for byte what the real package does, which is what makes it
attributable to the uninstall alone. Its two rows go red together because they
are two readings of one fact.

`eventlog-start-install` is a **table control**: the package is really built from
the real authoring, its own `ServiceControl` table is really read, and it is
deliberately never installed. The reason is a measurement that has changed. W0
§5.2 recorded `Start="install"` failing with 1920 and rolling the install back to
1603 — but that was measured against the console executable, which never called
`StartServiceCtrlDispatcher`. Since W3-A0 the executable answers the SCM from a
shell that has nothing to do first, so the same package can now *also* install
cleanly and leave the service Running. Both outcomes are the defect and neither
is the other, so installing it would grade the control on whichever one the
runner happened to produce. What the defect **is** — the package asking the
installer to start the service inside its own transaction — is in the package's
`ServiceControl` table either way, and that is what is read. It is the same
instrument and the same reasoning W4-A4's own `upgrade-starts-service` control
already uses.

The ruling's fourth control, **"UpgradeCode bytes moved"**, is not a run at all:
it is `ci/windows/w4/msi-controls.py`'s frozen-GUID gate, which refuses the
authoring before a package is built, and whose own self-test proves it fires for
a replaced GUID, for the same digits in a different case, and for the same digits
in braces.

## 6. Static controls

`ci/windows/w4/msi-controls.py` gained a preprocessor reducer, `real_authoring`,
and it is worth naming because it changes what every earlier authoring gate
means. The file carries its own hostile controls as `$(var.Mutation)` branches —
that is what makes them drive the real authoring — so a gate reading the raw text
cannot say what the **real** package is authored to be. `real_authoring` runs the
WiX preprocessor's own branch selection for `Mutation` = `none` and leaves every
other conditional alone.

One existing gate was **narrowed** rather than added. Before this slice it was
`'Start="install"' in text` — nowhere in the file. The W4-A6 ruling's own hostile
control is a package that starts the service inside the transaction, so the
property is now stated where it was always meant:

* `Start="install"` does not appear in the package the real build emits;
* it appears at most **once** in the whole file;
* and that one occurrence is inside the `eventlog-start-install` branch.

That is strictly stronger than the old gate on the first point and no weaker on
the others. The new gates assert the source key, the `ForceDeleteOnUninstall`,
the message file, the two values, the feature reference, that the real
component is not `Permanent`, that no log other than `Application` is
registered, that all three controls are reachable from the authoring **and**
accepted by the builder, and that the probe and the instruments module between
them start the service, stop it, read an event back, state the source key and
check the message file. Sixteen self-test mutations prove each one fires.

## 7. What is deliberately not claimed

* **No application or media events.** §4 gives the Event Log service-lifecycle
  events *only*, and shaping what the server itself writes is not this slice.
  `AddWindowsService`'s own Event Log logger provider is exactly as W3-A0 left it.
* **No signing**, no certificate, no release.
* **No repair** and **no upgrade path**. What an upgrade does to the source is
  not measured here.
* **No claim about W4.** This slice claims W4-A6 and no part of the stage.
* **No new event log.** The source is registered under the existing `Application`
  log; the package defines no log of its own.
* **No reproducibility claim.** W0 §5.6 measured that MSI bytes are not
  bit-for-bit and accepted a bounded exception.

## 8. Findings recorded rather than fixed

* **The 1920 path is no longer observed.** W0 §5.2's measurement predates W3-A0
  and this slice does not repeat it, for the reason §5 gives. What a package that
  starts the service inside the transaction actually does on a current runner is
  now an open question rather than a settled one, and it is worth one deliberate
  measurement in W5 rather than a coin flip inside a control.
* **`AddWindowsService`'s Event Log logger provider is still the framework
  default.** It writes under the source named after `IHostEnvironment.
  ApplicationName`, which is `tesserafin`; registry key names are
  case-insensitive, so it resolves to the same source this package registers. It
  is not graded here, and §4's "service-lifecycle events only" is satisfied by
  `ServiceBase`, not by it.
* **The message file is not read back.** `messageFileInstalled` asserts the file
  exists; nothing asserts the event's rendered text came from it rather than from
  a fallback. The dump prints each event's `Message` so a reader can see it.
* **Nothing measures a source registered by an *earlier* version of the
  package.** The probe resets the key between runs precisely so that no run
  inherits another's, which is the right choice for attribution and leaves the
  upgrade question to whoever asks it.

## 9. Where it runs

`.github/workflows/w4-windows-msi.yml`, job `msi-eventlog`, on a native
`windows-latest` runner. `contents: read` and `packages: read`, job-scoped, and
`packages: read` only because the frozen W2-A2 assembler pulls the accepted Web
payload image with the job's own token. Nothing is uploaded and nothing is
consumed as an artifact. The two existing jobs are untouched.

## 10. Authored surface

| Path | What changed |
| --- | --- |
| `packaging/windows/msi/Tesserafin.wxs` | the `EventLogSource` component group, three `Mutation` branches, one `ComponentGroupRef` |
| `ci/windows/w4/build-msi.ps1` | three values added to the control-only `-Mutation` set |
| `ci/windows/w4/W4MsiAssertions.psm1` | `Get-W4EventLogPredicates` / `…ControlExpectations` / `…Verdict` |
| `ci/windows/w4/W4MsiInstruments.psm1` | `Get-W4EventLogSource`, `Test-W4EventLogSourceExists`, `Get-W4LifecycleEvents` |
| `ci/windows/w4/probe-msi-eventlog.ps1` | new — the hosted proof |
| `ci/windows/w4/assertion-self-test.ps1` | the third grader, and five blind-spot assertions |
| `ci/windows/w4/msi-controls.py` | `real_authoring`, the W4-A6 gates, the narrowed `Start="install"` gate |
| `.github/workflows/w4-windows-msi.yml` | the `msi-eventlog` job |
| `docs/distribution/W4-A6-eventlog-source.md` | this document |

#234 stays open; this slice claims W4-A6 only, and claims no part of W4 accepted.
