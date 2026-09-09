# W4-A0 — the WiX MSI skeleton

**Tracker:** [#234](https://github.com/tesserafin-project/tesserafin/issues/234).
**Authorising ruling:** *OWNER RULING — W4-A0 WIX SKELETON*, 2026-09-09, from
master `fd8187f15ed7aa99552971305d5ac3f414c4d958`.
**Design this implements:** `docs/distribution/W0-windows-server.md` §4 (the
service contract), §5 (the installer decision) and §9 (the filesystem and
identity contract).

This slice proves one thing:

> a WiX project in-tree produces an MSI that installs the accepted `win-x64`
> server layout, creates the service `Tesserafin` with `--service` and the §4
> argument list, and uninstalls the service and the binaries while leaving the
> state directories.

Everything else W4 owes is a later slice, and this document is careful to say so
in each place a reader might otherwise infer a claim.

---

## 1. What this slice does not claim

| Not claimed | Where it is settled instead |
| --- | --- |
| a bit-identical MSI | W0 §5.6 already measured that MSI bytes are **not** bit-for-bit and accepted a bounded exception for two container fields. This slice does not build the same package twice, does not compare digests, and makes no reproducibility statement at all |
| signing | W0 §11. No certificate exists and none is created |
| upgrade, repair, downgrade, Add/Remove Programs | beyond what `MajorUpgrade` emits by default, none of those paths is driven. W0 §10 defines them; a later W4 slice measures them |
| the §9.3 ACLs | this package breaks no inheritance and grants nothing. W0 §9.2 is explicit that the permissive default `%ProgramData%` ACL is *not* a reason to rely on it, and that is exactly why the grant is a slice of its own rather than a line added here |
| an Event Log source as a separate product | not in this slice |
| W3 or W4 acceptance | neither. W3 landed A0 and A1 only; this is W4's first slice |
| any change to `Tesserafin.Server` | none. `AddWindowsService` is consumed as W3 accepted it and is not retuned |
| a hardware-acceleration capability | hosted runners have no GPU |

**The MSI identity was not a claim when W4-A0 landed; the `UpgradeCode` is now
frozen.** The package name `Tesserafin Server`, the manufacturer
`Tesserafin project` and the four retained-state component GUIDs remain the
identity this skeleton happened to build with, and each still deserves an
explicit decision before 1.1 ships. The `UpgradeCode`
`0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05` no longer does: the W4-A1 owner ruling
on #234 ratified that exact string — ordinal, lowercase, no braces — as the 1.1
UpgradeCode, and `ci/windows/w4/msi-controls.py` now reddens any other GUID,
including the same digits in a different case or wrapped in braces. It is
written into the authoring rather than generated per build because it is the one
value a later slice cannot change without stranding machines that already have
the product installed. See `docs/distribution/W4-A1-upgradecode.md`.

The `ProductVersion` is the version the commit already declares in
`SharedVersion.cs` — `1.0.0` — read through the same regular expression the
frozen W2-A2 assembler uses, so the MSI and the portable ZIP cannot disagree
about what they are. It is not the 1.1 release name and is not a version
decision.

---

## 2. What was authored

| File | What it is |
| --- | --- |
| `packaging/windows/msi/Tesserafin.wxs` | the WiX authoring: the harvested payload, the service registration, and the four retained-state components |
| `ci/windows/w4/build-msi.ps1` | the only thing that invokes the authoring. Pins the toolset, reads the accepted layout, and refuses a build whose authoring and accepted layout disagree |
| `ci/windows/w4/W4MsiAssertions.psm1` | the 24 predicates the package is graded on, and the RED set each hostile control must produce. Pure functions: an observation in, a verdict out |
| `ci/windows/w4/assertion-self-test.ps1` | drives that grader over synthetic observations, on any platform, in under a second |
| `ci/windows/w4/probe-msi-skeleton.ps1` | the hosted proof: assemble, build, install, observe, uninstall, observe — five times |
| `ci/windows/w4/msi-controls.py` | the fifth hostile control, and the gates that need no runner |
| `.github/workflows/w4-windows-msi.yml` | `pull_request`, `windows-latest`, `contents: read` |

There is **no `.wixproj` and no second executable project.** The authoring is
plain `.wxs` compiled by `wix build`, and `wix` is a `dotnet tool` pinned by
exact version on its own command line. `Directory.Packages.props` is therefore
**untouched**: the ruling permits a pin there only if one is unavoidable, and it
is not.

---

## 3. The service contract, and the one place §4 is not literal

W0 §4's table is implemented as written, with one deliberate substitution.

| §4 row | In the package |
| --- | --- |
| service name | `Tesserafin` |
| display name | `Tesserafin Server` |
| description | `Tesserafin media server. Manage it at http://localhost:8096.` |
| startup mode | `Start="auto"` plus `ServiceConfig/@DelayedAutoStart`, read back out of the SCM's own registry key rather than asserted |
| identity | `NT SERVICE\Tesserafin` |
| arguments | `--service`, then `--configdir`, `--datadir`, `--cachedir`, `--logdir` under `%ProgramData%\Tesserafin\Server\`, then `--webdir` and `--ffmpeg` under the install prefix |

**The substitution.** §4's table illustrates the encoder path as
`…\Server\ffmpeg\ffmpeg.exe`. The accepted W2 package puts it at
`ffmpeg\bin\ffmpeg.exe`, and that is what the MSI states — because the MSI does
not restate the layout at all. `build-msi.ps1` and the probe both read
`SERVER_RELATIVE_EXE`, `WEB_RELATIVE_DIR`, `FFMPEG_RELATIVE_EXE` and
`SERVICE_NAME` out of `ci/windows/w2/tesserafin-server-service.ps1` — the
accepted W2-A5 script that registers the service for the portable ZIP — and
`build-msi.ps1` **refuses to build at all** if the authoring's literal text does
not agree with those four constants. W3's probe reads the same constants from
the same file for the same reason. A second statement of the layout is exactly
the thing that would surface, much later, as a service that starts and finds no
Web tree.

`--webdir` and `--ffmpeg` are always explicit, so the service can never fall
back to a `PATH` encoder or a stale Web directory. `--nowebclient` is never
used.

**The service is not started.** W0 §10: a fresh installation leaves it
"installed and enabled but not started — an operator decides when a media
server begins serving". There is deliberately no `Start="install"` on the
`ServiceControl`: W0 §5.2 recorded that starting inside the transaction failed
with 1920 and rolled the whole install back to 1603, hiding install, upgrade,
repair and uninstall behind one identity problem.

`WIX1149` is expected on every build — WiX warns that the core `ServiceConfig`
element is documented in the Windows Installer SDK as not working as expected.
That warning is precisely why `serviceStartIsDelayed` is a **measurement**: the
probe reads `DelayedAutostart` back out of
`HKLM\SYSTEM\CurrentControlSet\Services\Tesserafin` after the install rather
than taking the authoring's word for it.

---

## 4. Retained state, expressed in the package

W0 §9.1 splits package-owned `%ProgramFiles%` from operator-owned
`%ProgramData%`, and §10 requires that an ordinary uninstall keep configuration,
database, cache and logs.

The four state directories are created by the package as `Permanent`,
`NeverOverwrite` components. That is what makes "uninstall does not delete them"
a property **of the package** rather than of a custom action that could be
skipped — and it is also what makes the claim measurable rather than vacuous:
the probe writes a sentinel file into each directory between install and
uninstall, and asserts all four directories and all four sentinels are still
there afterwards. Without directories the package created, "the uninstall left
them alone" would be a statement about nothing.

The state root is **not** redirected during the proof. §4's argument list names
`%ProgramData%\Tesserafin\Server`, and a run that redirected it would be
measuring an argument list no operator will ever receive.

---

## 5. How the package under test is acquired

The package is assembled **in the job**, from the pull request head, by the
frozen W2-A2 assembler `ci/windows/w2/assemble-server-zip.ps1`, and the
resulting ZIP is extracted to give the stage the MSI harvests.

Nothing is downloaded from a tag, from an Actions artifact or from "the latest".
What is consumed by accepted digest is what W2 pins — the Web payload and the
FFmpeg runtime — through the frozen consumers the assembler drives from its own
committed acceptance manifest. `build-msi.ps1` deliberately declares **no**
`-Tag`, `-RunId`, `-Reference`, `-Url` or `-Digest` parameter, and
`msi-controls.py` asserts it never grows one: the whole security property of the
frozen consumers is that the identity of what is packaged travels with the
commit.

Two files in the stage are deliberately held back from the harvest:

* `tesserafin.exe`, which is authored explicitly instead, because the service
  registration has to live on the component that owns the executable and a
  harvested component cannot carry one. It is still sourced from the same
  stage, so the harvested payload and the authored executable cannot come from
  different packages;
* `tesserafin-server-service.ps1`, which is not installed at all. W0 §6 gives it
  to the portable ZIP as a convenience over the same §4 contract and is explicit
  that it "is **not** a second installer". Shipping it inside the MSI would put
  a second registration path on a machine that already has the packaged one.

They are held back by **materialising a separate harvest directory** — the
accepted stage minus those two files — rather than by an exclude list inside the
authoring. WiX harvesting is a linker behaviour, so an exclude that failed to
match would deliver the executable twice into one directory and fail an ICE, and
it would do so on the runner after the publish rather than anywhere the
authoring could be checked first. `build-msi.ps1` materialises that directory
once, reuses it for all five builds, and re-validates on every call that it
holds neither withheld file, that it still carries the Web tree and the encoder,
and that no other top-level entry of the stage went missing along the way.

---

## 6. The grader, and why it is graded first

The package is graded on 24 predicates covering containment, the installed
layout, all eight service-contract properties, the argument list token by token,
and the uninstall. They live in `W4MsiAssertions.psm1` as pure functions, and
the four hostile controls' expected RED sets are declared beside them.

A control table is only evidence if the grader can tell its controls apart. A
grader that answered "red" to everything, or "green" to everything, would
produce a table that looks exactly as convincing — and that failure mode is
invisible from a green run. So `assertion-self-test.ps1` drives the grader over
synthetic observations first, on any platform, and requires that:

* the correct package reddens nothing;
* each control reddens **exactly** its declared set — no more (a control that
  broke something else as well is attributable to nothing) and no less (a
  control that did not reproduce its defect proves nothing);
* every mutation produces a **different** red set. Two controls with identical
  failure lists is the tell that the harness graded nothing.

The hosted probe repeats that distinctness check over the five real runs.

---

## 7. The hostile controls

Four are properties of an installed package and are measured on the runner. Each
builds the **same authoring** with one deliberate defect, through the same
builder and the same grader, rather than a second copy written for the test —
the same reason the frozen W2-A2 assembler carries its own control-only
parameter set. `msi-controls.py` asserts the hosted acceptance build passes no
mutation.

| Control | The defect | Declared RED |
| --- | --- | --- |
| `no-exe` | the server executable is delivered under a different name, so the package contains no `tesserafin.exe`. The service registration is left intact so that containment reddens and the argument list does not | `msiContainsServerExe`, `installedServerExe`, `serviceImagePathIsInstalledExe` |
| `no-service-flag` | the argument list omits `--service` | `serviceImagePathHasServiceFlag` |
| `no-path-flags` | the argument list omits `--webdir` and `--ffmpeg` | `serviceImagePathHasWebDir`, `serviceImagePathHasFfmpeg` |
| `no-service-remove` | the `ServiceControl` element is absent, so the uninstall leaves the service registered | `uninstallRemovedService` |

The fifth — *workflow `write-all`, or a production artifact reused as a later
input* — is a property of the authored files and needs no runner, so
`msi-controls.py` measures it in a second, on any machine, and proves each of
its own gates can fire by mutating a copy of the workflow five ways. The
permission check parses YAML rather than grepping it: `write-all` and a quoted
`"packages": "write"` both grant, and both slip past a grep gate.

The workflow's entire write surface is `contents: read` at the top level and
`contents: read` plus `packages: read` on the job. `packages: read` is needed
and only needed because the frozen assembler pulls the accepted Web payload
image with the job's own token, exactly as W3's job does. **Nothing is
uploaded** — this slice produces no artifact for any later step to consume.

---

## 8. The install prefix

The ruling asks that the package be installed under a disposable prefix rather
than the runner's real `%ProgramFiles%`, and that the run record which it got.

`INSTALLFOLDER` is a public directory property, so the probe redirects it on the
`msiexec` command line to a directory under `RUNNER_TEMP`, one per run. That the
redirection actually **took** is a predicate rather than an assumption:
`installedOutsideProgramFiles` asserts `%ProgramFiles%\Tesserafin` does not
exist after the install, and the probe refuses before it starts if that
directory is already there — otherwise the run could not tell a real
`%ProgramFiles%` install apart from something that was already on the host.

The evidence document records the answer in `installPrefixKind`, read off the
measurement rather than off the intent. There is deliberately **no** automatic
fallback to `%ProgramFiles%`: if the override did not take, that is the finding.

---

## 9. What a reviewer should check next

* the `UpgradeCode` above, which needs an explicit ruling before 1.1 ships;
* whether `ServiceConfig/@DelayedAutoStart` actually produced
  `DelayedAutostart = 1` on the runner, or whether `WIX1149` was a real warning
  and the delayed half needs `WixToolset.Util.wixext` — which would be the first
  extension dependency this packaging takes on;
* the retained-state components' GUIDs, which are as load-bearing as the
  `UpgradeCode` for the retained-data policy;
* that §4's illustrative `ffmpeg\ffmpeg.exe` and the accepted package's
  `ffmpeg\bin\ffmpeg.exe` are reconciled in W0 itself, so the next reader does
  not have to rediscover which one is real.
