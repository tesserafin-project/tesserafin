# W3-A0 — the Generic Host service boundary and the non-zero fatal exit

Tracker: [#234](https://github.com/tesserafin-project/tesserafin/issues/234).
Ruling: `OWNER RULING — W3-A0 SERVICE HOST BOUNDARY`, 2026-09-09, on #234.
Contract: [`W0-windows-server.md`](W0-windows-server.md) §2.5 and §4.
Frozen starting master: `81ee1b00d659109aefe46cf862edcd45ee0e46be`, which is
W2-CLOSEOUT as accepted.

This slice closes two W0 findings and nothing else:

1. the unmodified `tesserafin.exe`, registered with the Service Control Manager
   and started, **fails with error 1053** because a plain console process never
   calls `StartServiceCtrlDispatcher` (§4);
2. a fatal startup — `FfmpegException` on a host with no encoder — **exits 0**,
   which the SCM reads as a service that stopped normally (§2.5).

It is not an installer, not an identity, not an ACL grant and not a machine-wide
setting. W4 owns all four.

---

## 1. What changed in the server

| File | Change |
| --- | --- |
| `Directory.Packages.props` | pins `Microsoft.Extensions.Hosting.WindowsServices` at `10.0.9` |
| `Tesserafin.Server/Tesserafin.Server.csproj` | references it |
| `Tesserafin.Server/Program.cs` | the `--service` branch, the exit contract, and an external shutdown request |
| `Tesserafin.Server/ServiceHost/WindowsServiceEntryPoint.cs` | new — the Windows service shell |
| `Tesserafin.Server/ServiceHost/WindowsServiceServerRunner.cs` | new — the hosted service that runs the server under it |
| `tests/Tesserafin.Server.Tests/ServiceHost/FatalStartupExitCodeTests.cs` | new — the exit contract as process-level evidence |

`Directory.Packages.props` is **not** in the ruling's authorized path list. It is
edited because it cannot be avoided: this repository manages package versions
centrally, so a `PackageReference` to a package with no `PackageVersion` is a
`NU1010` restore error, and `VersionOverride` overrides a pin rather than
creating one. §4 selects `AddWindowsService`, `AddWindowsService` lives in that
package, and there is no third option. One line, one package, pinned to the same
`10.0.9` its `Microsoft.Extensions.Hosting.Abstractions` sibling already carries.

### 1.1 `--service` is wired to `AddWindowsService`, and to nothing else

```
OperatingSystem.IsWindows() && options.IsService && WindowsServiceHelpers.IsWindowsService()
```

All three, deliberately. `--service` is the operator's declaration and
`IsWindowsService()` is the environment's answer; requiring both is what makes
`tesserafin --service` from a console byte-for-byte the thing it was before this
slice. On every other platform and in every console invocation the branch is not
taken and `RunServerAsync` is the same code path master already had.

### 1.2 The shell, and why it is not simply `AddWindowsService` on the server's own host

The obvious change — add `AddWindowsService` to the host builder in
`StartServer` — does not work, and the reason is timing rather than taste.

`Host.StartAsync` awaits `IHostLifetime.WaitForStartAsync` **before** it starts
any hosted service, and that is where `WindowsServiceLifetime` calls
`ServiceBase.Run` and answers the SCM. On the server's own host that call sits at
`Program.cs`'s `_reefinHost.StartAsync()`, which is reached only *after*
`ApplyStartupMigrationAsync`, `PrepareSystemForMigration`, the
`CoreInitialisation` migration step, `InitializeServices` and the
`AppInitialisation` step. W0 §2.3 measured a cold first start on this same runner
image **still applying migrations at 180 s**. The SCM's `ServicesPipeTimeout` is
30 s by default, and raising it is a machine-wide registry write this slice is
explicitly not permitted to make. A host builder that reaches the dispatcher only
after the migrations would therefore reproduce, on a first start after a fresh
install, the exact 1053 it was meant to close — and would pass a CI job that
happened to start warm.

So the service branch builds a **shell**: a bare `HostBuilder` with
`AddWindowsService` and exactly one hosted service. It has nothing to do before
`WaitForStartAsync`, so the SCM handshake completes in about a second regardless
of the database. The hosted service then starts the real server on the thread
pool and returns immediately. This is still §4's "direct .NET Generic Host
Windows Service integration in `Tesserafin.Server`": one executable, one process,
one lifetime owned by the framework's own `WindowsServiceLifetime`. It adds no
second executable, no third-party wrapper and no second `ServiceBase`.

### 1.3 The exit contract

`StartServer` already caught every fatal startup exception, logged it and
returned. It now also carries a non-zero code out:

* `StartServer` returns an `int`; the fatal `catch` sets
  `Program.StartupFailureExitCode` (`1`).
* `RunServerAsync` returns the last iteration's code, so the restore-from-backup
  restart loop cannot lose it.
* `StartApp` sets `Environment.ExitCode` and calls `Environment.Exit` **only on
  the non-zero path**, so the code is a fact rather than an intention if
  something else would have kept the process alive.
* Under the SCM, `WindowsServiceServerRunner` additionally sets
  `WindowsServiceLifetime.ExitCode` before requesting the stop.

The last point is the one that is easy to get wrong. The SCM reads two different
things depending on how the process ends. If it dies without reporting
`SERVICE_STOPPED`, the SCM records its own `1067` and the process exit code is
what a script or an operator sees. If the host stops cleanly, the SCM reads
`ServiceBase.ExitCode` — which defaults to `0`, and would report precisely the
"stopped normally" that §2.5 names as the defect. Setting both is what makes a
fatal startup non-zero in either shape.

**Console exit 0 is unchanged.** A clean `Ctrl+C` after a successful start leaves
`WaitForShutdownAsync` normally, never enters the `catch`, and returns `0`
exactly as before. `OrdinaryExit_StillExitsZero` is the control that says so.

The contract deliberately covers **every** fatal startup exception rather than
special-casing the encoder. A server that could not start is a failed start
whatever killed it, and a contract keyed to one exception type would go quiet the
moment the encoder check moved.

### 1.4 The stop path

`WindowsServiceServerRunner.StopAsync` asks the server to shut down and then
awaits it, so the service does not report `STOPPED` while its own process is
still writing to the database. The request travels through a
`CancellationTokenSource` on `Program` rather than by reaching for `_reefinHost`
directly, because a stop can arrive while the server is still migrating and the
host does not exist yet; the registration made after `Build()` fires inline when
the request already arrived.

The shell's `HostOptions.ShutdownTimeout` is set to §4's tabulated **120 s** so
that the shell is never the tighter of the two bounds. That is an in-process
value only. §4's stop timeout is *also* a machine-wide `ServicesPipeTimeout`,
which an installer writes and this slice does not. The server's own host keeps
its default timeout, so console shutdown timing is untouched.

---

## 2. The evidence

### 2.1 Process-level, on any platform, no SCM

`tests/Tesserafin.Server.Tests/ServiceHost/FatalStartupExitCodeTests.cs` runs the
real entry point as a child process, because the property under test is a
property of the process. Both tests give the child fresh state directories, an
empty `PATH`, no `--ffmpeg` and no `TESSERAFIN_*` environment, and reach the
`dotnet` host by absolute path so that the empty `PATH` is not self-defeating.

| Test | What it asserts |
| --- | --- |
| `MissingEncoder_FatalStartup_ExitsNonZero` | exit code ≠ 0 **and** `FfmpegException` in the output |
| `OrdinaryExit_StillExitsZero` | `--mode MigrateSystem` still exits `0` |

Both halves of the first assertion are load-bearing. The exit code alone would
also be satisfied by a Kestrel bind failure or a bad path, and a test that
accepted any failure would keep passing if the encoder path stopped being fatal.
The port is pinned to a free ephemeral one through the real
`NetworkConfiguration` type, because the encoder check runs *after* Kestrel binds
and anything already listening on 8096 would otherwise turn a proven encoder
failure into an unexplained one.

`OrdinaryExit_StillExitsZero` is the control that says the non-zero code is a
property of the failure and not a new property of exiting. `--mode MigrateSystem`
runs the migrations and shuts down without starting Kestrel and without running
the startup tasks, so it needs no encoder and binds no port: an ordinary exit by
construction, on the same tree and the same absent `PATH`.

**Mutation control.** With `exitCode = StartupFailureExitCode` reverted to
`exitCode = 0` — master's behaviour — `MissingEncoder_FatalStartup_ExitsNonZero`
fails with `Assert.NotEqual() Failure: Values are equal` and
`OrdinaryExit_StillExitsZero` stays green. The test observes the change rather
than the tree.

### 2.2 On a real Service Control Manager

`.github/workflows/w3-windows-service-host.yml` runs
`ci/windows/w3/probe-service-host.ps1` on `windows-latest`, `pull_request` only,
`permissions: contents: read` plus a job-scoped `packages: read` for the accepted
Web payload pull. Three controls, one package:

| | Control | Asserts |
| --- | --- | --- |
| **P** | the package's own `tesserafin-server-service.ps1`, unmodified, with §4's full argument list | `sc start` succeeds, **no 1053**, `RUNNING`, the server answers on a port it bound, `sc stop` leaves `Stopped` with exit code `0`, no surviving process |
| **N** | the same exe and `--service`, `--ffmpeg` withheld | ends `Stopped` with a **non-zero** exit code, `FfmpegException` in the service's log, no surviving process |
| **C** | N's command line with `--service` removed | **1053 still reproduces** |

#### Measured

On head `b8f72ecd31`, `windows-latest` (`Microsoft Windows NT 10.0.26100.0`,
PowerShell 7.6.5), package `451cd38a…` / server exe `552bd718…` built in-job,
web payload `4148c4bc…` and FFmpeg runtime `f28cc918…` consumed by accepted
digest. The runner carried **no** `ffmpeg` on `PATH`, so N's premise held.

| | P | N | C |
| --- | --- | --- | --- |
| `sc start` | exit `0`, `START_PENDING`, 0.3 s | exit `0` (expected) | **exit `1053`, 6.1 s** |
| error 1053 | **false** | — | **true** |
| state reached | `Running`, PID 3460 | `Stopped` | `Stopped` |
| readiness | `/` → `302` on 8096, alive 3 s later | — | — |
| exit code seen by the SCM | `0` after `sc stop` | **`1`** | — |
| `FfmpegException` in the log | — | **yes** | — |
| orphaned processes | `0` | `0` | `0` |

W0 §4 measured this same executable failing SCM start with **1053 after 7 s**.
With `--service` the handshake completes in **0.3 s**; without it, on a command
line otherwise identical to N's, 1053 still reproduces. The fatal startup that
master exits `0` on is now `Stopped` with `WIN32_EXIT_CODE 1`.

**C is what makes P attributable.** Without it, P proves only that the server
starts; a build in which the boundary had been wired unconditionally — or a
runner on which 1053 had stopped happening for an unrelated reason — would be
indistinguishable from the change this slice made.

**N does not assert that `sc start` fails, and asserting that would be wrong.**
`Host.StartAsync` answers the SCM before it starts any hosted service, so by the
time the encoder check runs — inside the startup tasks, after Kestrel has bound —
the service has already reported `RUNNING`. A service host that reported a failed
*start* here would be one that had not answered the SCM yet, which is the 1053
this slice closes. §2.5's requirement is that "the SCM sees a failed start, not a
normal stop", and that is where the assertion lives: in the stop, on a non-zero
exit code. The `sc start` result is recorded verbatim, not graded.

Service state is read from `Win32_Service` through CIM rather than parsed out of
`sc.exe query` text — W2-A5's `NB-3` records that the `STATE` label is
localisable, and `ExitCode` and `ProcessId` are numbers there rather than table
cells. Readiness is W0 §2.3's, with all four traps closed: the port comes from
the process's own listening sockets, a redirect is an answer, a `503` from the
startup `SetupServer` is not readiness, and the process must still be alive three
seconds after it answered.

### 2.3 Which executable answered the SCM

The archive is assembled **in the job** by the frozen W2-A2 assembler, from the
pull request head. That is deliberate. What W2 pins and what this job consumes by
accepted digest are the Web payload (`4148c4bc…`) and the FFmpeg runtime
(`f28cc918…`), which the assembler reads from its own committed acceptance
manifest and refuses to substitute. The archive's `tesserafin.exe` has always
been built from the tree being packed, and at the accepted W2 master that exe is
the *stock* one — it reproduces 1053 by construction, so a run against it could
only ever be a false red. The exe's SHA-256 is recorded in the evidence document
so a reviewer can see which binary answered the SCM. Nothing is uploaded.

The W2 scripts are run unmodified and uncopied: `assemble-server-zip.ps1` from
the repository, and `tesserafin-server-service.ps1` from inside the extracted
archive, which is where it ships.

---

## 3. Named residuals — recorded, not repaired

None of these is a condition of this slice, and none is authorized here.

* **The pre-`configurationCompleted` failure path still lingers.** A startup
  failure that happens *before* the setup server hands over waits ten minutes
  serving its error page (`Program.cs`, the `catch` block). Under the SCM that is
  1053 **plus an orphaned process**, which W0 §4 calls worse than a clean
  failure. It is not on A0's path — `FfmpegException` fires from
  `RunStartupTasksAsync`, by which point `configurationCompleted` is `true` — but
  it is the next thing W3 should look at.
* **The in-process restart loop is untested under the SCM.**
  `_restartOnShutdown` (restore-from-backup) restarts the server inside the same
  process. The shell survives that correctly, because it owns the only
  `ServiceBase`, but no control here exercises it under a service.
* **Started under the SCM without `--service`, the server still 1053s.** That is
  control C, and it is by design: §4 always passes `--service`. It is recorded
  because an installer that omitted the flag would produce a failure that looks
  like a packaging defect.
* **`AddWindowsService` adds an Event Log logger provider.** §4 wants the Windows
  Event Log for service-lifecycle events only; what is registered here is the
  framework default, and the server's own logging is Serilog to the log
  directory. Shaping the event log is a later slice. Under a non-administrator
  service identity an unregistered event source would also need the installer to
  create it — W4, together with §9's identity.
* **The stop timeout is not measured.** W0 §2.6 defers the worst-case shutdown —
  with a transcode running, under the SCM — to a W3 measurement. This slice
  adopts §4's tabulated 120 s for the shell and measures nothing; the number is
  still owed.
* **`W2-A5` `NB-2` is now partly measured.** The `sc.exe binPath=` quoting that
  A5 recorded as correct-but-unobserved is exercised here, in both the accepted
  script's `register` and the probe's own raw `sc create`. A5's other retained
  limitations are untouched.

---

## 4. Status

Both halves are measured: the process-level exit contract on Linux and on
`windows-latest`, and all three SCM controls on a native Windows host. #234 stays
open; this slice claims W3-A0 only, and claims no part of W3 accepted.
