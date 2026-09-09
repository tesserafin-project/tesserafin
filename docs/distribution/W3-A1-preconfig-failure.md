# W3-A1 — no linger on a pre-configuration service failure

Tracker: [#234](https://github.com/tesserafin-project/tesserafin/issues/234).
Ruling: `OWNER RULING — W3-A1 PRE-CONFIG FAILURE UNDER SCM`, 2026-09-09, on #234.
Contract: [`W3-A0-service-host.md`](W3-A0-service-host.md) §3, first residual.
Frozen starting master: `e463d92650efc2c0ebfc15d6d321db543954f361`, which is
W3-A0 as accepted.

This slice closes one named residual and nothing else:

> **The pre-`configurationCompleted` failure path still lingers.** A startup
> failure that happens *before* the setup server hands over waits ten minutes
> serving its error page (`Program.cs`, the `catch` block). Under the SCM that is
> 1053 **plus an orphaned process**, which W0 §4 calls worse than a clean
> failure.

It is not an installer, not an identity, not an ACL grant, not a machine-wide
setting and not a stop-timeout measurement. It does not claim W3 accepted.

---

## 1. What lingers, and why it is a defect only under the SCM

`Program.StartServer` catches every fatal startup exception. If the setup server
is still the thing serving the browser — `IsAlive` and not `configurationCompleted`
— master marks it unhealthy and then waits:

```csharp
_setupServer!.SoftStop();
if (options.StartupMode is null or Configuration.StartupMode.MediaServer)
{
    await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
}
```

**In a console that wait is the feature.** A first start that fails before the
setup wizard has handed over is exactly the case where the operator has nothing
else to read: no configured log destination they know about, no server to ask,
and a browser already open on the setup page. Ten minutes of an error page is
the answer to "it just died".

**Under the Service Control Manager it is the opposite.** Nobody is holding a
browser open, and three things follow from the wait that do not follow in a
console:

* the service stays `RUNNING` for ten minutes with a dead server behind it, so
  the SCM's failure actions never fire and nothing records that the start
  failed;
* a `sc stop` issued in that window reaches `WindowsServiceServerRunner.StopAsync`,
  which awaits the server task — the task that is sitting in the `Task.Delay`.
  The shell's `ShutdownTimeout` is 120 s, so the host gives up and the process is
  abandoned rather than stopped: W0 §4's orphaned `tesserafin.exe`;
* the next start races that process for the ports and the database.

W0 §4 calls the orphan worse than a clean failure, and it is right: a clean
failure is something an operator can see in `sc query` and something the SCM can
act on.

## 2. What changed in the server

| File | Change |
| --- | --- |
| `Tesserafin.Server/Program.cs` | the linger becomes a named constant, the service predicate becomes one method, and the linger is skipped under the SCM |
| `tests/Tesserafin.Server.Tests/ServiceHost/FatalStartupExitCodeTests.cs` | two tests, in the class the hosted job already filters on |

Nothing else. `Directory.Packages.props` is **not** touched this time — A0's
`Microsoft.Extensions.Hosting.WindowsServices` pin is what this builds on, and no
new package is needed.

### 2.1 One predicate, two callers

```csharp
[SupportedOSPlatformGuard("windows")]
internal static bool IsRunningAsWindowsService(StartupOptions options)
    => OperatingSystem.IsWindows() && options.IsService && WindowsServiceHelpers.IsWindowsService();
```

A0 wrote that conjunction inline in `StartApp`. It now has one home, because the
entry point and the failure path have to answer it identically: a process that
took the service *host* but not the service *failure semantics* is precisely the
orphan this closes. The `[SupportedOSPlatformGuard]` attribute is not decoration
— without it, extracting the conjunction hides `OperatingSystem.IsWindows()`
from the platform analyzer and the Windows-only service host becomes a CA1416
**error**, not a warning.

### 2.2 The decision is separate from the fact

```csharp
internal static bool ShouldLingerAfterPreConfigurationFailure(StartupOptions options, bool runningAsWindowsService)
    => !runningAsWindowsService
        && options.StartupMode is null or Configuration.StartupMode.MediaServer;
```

The startup-mode half is master's and is unchanged. The service fact is a
**parameter** rather than a call, and that is the whole reason any of this is
testable off a runner: `WindowsServiceHelpers.IsWindowsService()` answers `false`
in every process that is not an SCM service process, which includes the hosted
`dotnet test` step on `windows-latest`. No test anywhere can reach the service
branch of `IsRunningAsWindowsService`. What a test can reach is the decision it
feeds.

### 2.3 The stop is not skipped

```csharp
if (_setupServer!.IsAlive && !configurationCompleted)
{
    _setupServer!.SoftStop();
    if (ShouldLingerAfterPreConfigurationFailure(options, IsRunningAsWindowsService(options)))
    {
        await Task.Delay(PreConfigurationFailureLinger).ConfigureAwait(false);
    }

    await _setupServer!.StopAsync().ConfigureAwait(false);
}
```

Only the `Task.Delay` is conditional. `SoftStop` and `StopAsync` still run under
the SCM, because the setup server holds the HTTP port and it has to be released
before `StartServer` returns.

**The exit code is untouched.** A0 already set `exitCode = StartupFailureExitCode`
at the top of this `catch`, and `WindowsServiceServerRunner` already publishes it
on both channels the SCM reads. What master lacked was not a code but a chance to
report it before the SCM had stopped caring. Removing the wait is what makes the
existing code reachable in time. `StartApp` then calls `Environment.Exit`, which
is why "the process is gone" and "the exit code is non-zero" are the same event.

## 3. The fault used to prove it

The ruling requires the real hook to be named rather than a flag invented for
the test. It is:

```
Tesserafin.Common.Configuration.EncodingConfigurationExtensions.GetTranscodePath
```

called from `Program.StartServer` as the **first statement after the host is
built**, long before `configurationCompleted = true`:

```csharp
_ = appHost.ConfigurationManager.GetTranscodePath();
```

`GetTranscodePath` reads `TranscodingTempPath` out of `encoding.xml` and creates
the directory if it is missing. Both the local test and control F point that
setting at a path whose parent is a **regular file**, so `Directory.CreateDirectory`
cannot create it and throws. That is a real operator misconfiguration reached
through the real configuration file — the shape a moved or half-restored library
takes — and it needs no server change to provoke.

Measured locally on this tree, Linux, console, `--nowebclient`:

```
[12:26:38.822] [INF] Setting cache path: …/rehearse/cache
[12:26:40.317] [FTL] Main: Error while starting server
System.IO.DirectoryNotFoundException: Could not find a part of the path '…/rehearse/blocker/transcodes'.
   at System.IO.Directory.CreateDirectory(String path)
   at …AppBase.BaseApplicationPaths.CreateAndCheckMarker(String path, String markerName, Boolean recursive)
   at …Configuration.EncodingConfigurationExtensions.GetTranscodePath(IConfigurationManager configurationManager)
   at Tesserafin.Server.Program.StartServer(…)
```

1.5 s from the first log line to the failure, and the process was still alive
when it was killed 240 s later — the console linger, doing what it is for.

.NET names the two paths differently by platform: the unreachable path on Unix
(`Could not find a part of the path '…/transcodes'`), the colliding entry on
Windows (`Cannot create '…\not-a-directory' because a file or directory with the
same name already exists`). Both assertions therefore look for the **file's**
path, which is a prefix of the other and is the one substring both messages
carry.

## 4. The evidence

### 4.1 Local, on any platform, no SCM

Both tests are in `FatalStartupExitCodeTests` on purpose. The hosted job filters
by `FullyQualifiedName~FatalStartupExitCodeTests`; a new class with a new name
would have been reviewable without ever running on the runner.

| Test | What it asserts |
| --- | --- |
| `PreConfigurationFailure_UnderWindowsService_DoesNotLinger` | the decision is `true` for a console and `false` under the SCM, and `false` for both when the startup mode is not the media server |
| `PreConfigurationFailure_InConsole_StillLingers` | the real entry point, as a child process, with the fault of §3: it must log `Error while starting server` naming the blocking path, must **not** log `FfmpegException`, and must still be alive twenty seconds later |

The second test's window is measured **from the failure**, not from the launch,
so a loaded runner lengthens the wait for the marker instead of shortening the
observation.

Local run, this tree, `dotnet test --filter FullyQualifiedName~FatalStartupExitCodeTests`:
**4 passed, 0 failed, 31 s** — the two new tests and A0's two, which are
unchanged.

**Mutation control.** With the guard reverted — `ShouldLingerAfterPreConfigurationFailure`
returning master's condition, ignoring `runningAsWindowsService` —
`PreConfigurationFailure_UnderWindowsService_DoesNotLinger` fails with
`Assert.False() Failure / Expected: False / Actual: True` and the other three
stay green. `PreConfigurationFailure_InConsole_StillLingers` staying green under
that mutation is correct and is the point: it guards the console behaviour, which
the mutation does not change. The two tests are complementary rather than
redundant.

### 4.2 On a real Service Control Manager — control F

`ci/windows/w3/probe-service-host.ps1` gains a fourth control beside P, N and C.
None of those three is modified: no assertion is removed, relaxed or reordered,
and the only line F shares with them is the service list the cleanup iterates.

| | Control | Asserts |
| --- | --- | --- |
| **F** | `--service`, P's full argument list **including `--ffmpeg`**, and an `encoding.xml` whose `TranscodingTempPath` is blocked by a file | the blocking path is in the service log; `FfmpegException` is **not**; the service reaches `Stopped`; the time from the failure to `Stopped` is under 120 s; the SCM's exit code is non-zero; no `tesserafin` process survives |

**Why the encoder is present.** N withholds it. F must not, or the encoder path
would fire and F would silently become a second copy of N — which is why
`FfmpegException` in F's log is a refusal rather than an aside.

**Why the budget is measured from the failure.** F's hook fires after the startup
migrations, so a bound counted from `sc start` would be a bound on how fast the
runner creates a database, not on the linger. The probe polls the service log for
`Error while starting server` and the service state in the same loop, stamps
both, and asserts on the difference. Master waits 600 s there; the budget is
120 s, five times what the repaired path needs.

**F is not inert.** Every refusal path was exercised against a stubbed Service
Control Manager before the branch was pushed — the service never stopping, the
service stopping with exit code 0, and the linger exceeding its budget each
produced the expected `W3-A1 REFUSED [preconfig]` rather than a pass.

#### Where the numbers are

A0 tabulated its three controls in this document. This one does not, and the
reason is procedural rather than a judgement about evidence: the W3-A1 ruling
authorises **one ordinary push and no amend after it**, and control F cannot run
until that push exists. A table written before the push would be a prediction,
and a table written after it would need a second push.

The measurement is therefore read where the job writes it, and it is written to
be read whole. `ci/windows/w3/probe-service-host.ps1` records every control in
one evidence document — schema 2, `slice: "W3-A0 + W3-A1"`, with `F.preconfig`
carrying `faultLoggedAfterSeconds`, `stoppedAfterSeconds`, `lingerSeconds`,
`lingerBudgetSeconds`, `masterLingerSeconds`, the SCM's `stopped.exitCode`,
`hookInLog`, `ffmpegExceptionInLog` and `orphans` — and the workflow's
`Report the four controls` step prints that document in full. It holds states,
exit codes, ports, digests and durations only: no host path and no run
identifier.

The probe also prints one line per control as it goes, so a reader who wants
only the headline can read F's without opening the JSON:

```
W3-A1: F: fault logged at <t1> s, stopped at <t2> s (linger <t2-t1> s of a 120 s
budget; master waits 600 s), Win32 exit code <code>, orphans <n>
```

The job is red unless every assertion in §4.2 held, so a green run is the claim
and the document is the detail behind it.

## 5. Named residuals — recorded, not repaired

None of these is a condition of this slice, and none is authorized here.

* **The service branch of `IsRunningAsWindowsService` is unreachable from any
  test.** `WindowsServiceHelpers.IsWindowsService()` is `false` in every process
  the SCM did not start. The predicate is covered; the wiring from it to the real
  environment is covered only by F, and only on a runner.
* **The 120 s budget is a bound, not a measurement of the floor.** F records what
  the linger actually was, but nothing here asserts a *minimum*, so a future
  change that made the failure path slower by a minute would still pass.
* **A failure after `configurationCompleted` is untouched.** That path never
  entered the `if` and never lingered; A0's N is what covers it.
* **The in-process restart loop is still untested under the SCM.** A0 §3's second
  residual, unchanged.
* **The stop timeout is still not measured.** W0 §2.6's worst-case shutdown, with
  a transcode running, remains owed. F's 120 s budget is a linger bound and says
  nothing about it.
* **`AddWindowsService`'s Event Log provider, the identity and the ACL** remain
  W4's, exactly as A0 left them.

## 6. Status

The decision is proved locally on any platform, with a mutation control. The
no-linger observation itself is hosted-only, in control F, for the reason §2.2
gives. #234 stays open; this slice claims W3-A1 only, and claims no part of W3
accepted.
