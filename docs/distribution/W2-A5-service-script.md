# W2-A5 — First-party ZIP service script

Tracker: [#256](https://github.com/tesserafin-project/tesserafin/issues/256).
Umbrella: #234. Base master:
`5e307ce36e5baa4192f178f0fb8de2e58efe2afb`, which is W2-A4 as accepted.

This slice delivers the last outstanding bullet of the portable-ZIP contract.
[W0 §6](W0-windows-server.md) requires the archive to carry

> a first-party PowerShell script that registers, starts, stops and removes the
> service for operators who prefer the ZIP. That script is a convenience over
> the same contract as §4 — it is **not** a second installer and gets no repair,
> rollback or Add/Remove Programs entry, which are properties of the format that
> no script can add.

That sentence is the whole specification, and both of its halves are load-bearing:
the first says what the script must do, and the second says what it must never
become. **W2 is not accepted by this document.**

---

## 1. The frozen relative path

The script ships at the **top level of the package directory**:

```
tesserafin-server_<version>_win-x64/
├── tesserafin-server-service.ps1     <-- this slice
├── tesserafin.exe
├── web/
├── ffmpeg/bin/ffmpeg.exe
└── licenses/
    ├── LICENSE
    ├── provenance.json
    └── ffmpeg/sbom.cdx.json
```

That location is not a preference. The script derives every path it hands the
Service Control Manager from its own `$PSScriptRoot`, so the directory it lives
in *is* the package root. One level down — `tools/`, `service/` — would make the
package root an arithmetic result rather than the directory the operator is
standing in, and an arithmetic result is one refactor away from being wrong in a
way nothing measures. It also sits next to `tesserafin.exe`, which is where an
operator who has just extracted a ZIP looks.

`ci/windows/w2/assemble-server-zip.ps1` stages it from the checkout, hashes the
**staged** copy, and records the digest and the relative path in
`licenses/provenance.json`:

```json
"serviceScript": {
  "relativePath": "tesserafin-server-service.ps1",
  "sha256": "…",
  "contract": "W0 §6: a first-party PowerShell script that registers, starts, stops and removes the service, a convenience over the §4 service contract",
  "isInstaller": false,
  "providesRepair": false,
  "providesRollback": false,
  "writesAddRemoveProgramsEntry": false
}
```

The digest is taken from the staged file rather than from the checkout copy,
because "the file was copied" and "the file in the package is that file" are two
different statements and only the second is what the manifest goes on to claim.
The assembler **stages** the script and never runs it.

---

## 2. What the four verbs do

| Verb | What it asks the Service Control Manager |
| --- | --- |
| `register` | `sc.exe create Tesserafin binPath= "<package>\tesserafin.exe" --service --configdir … --datadir … --cachedir … --logdir … --webdir "<package>\web" --ffmpeg "<package>\ffmpeg\bin\ffmpeg.exe" start= delayed-auto DisplayName= "Tesserafin Server"`, then `sc.exe description` and `sc.exe failure` |
| `start` | `sc.exe start Tesserafin`, then waits for `RUNNING` |
| `stop` | `sc.exe stop Tesserafin`, then waits for `STOPPED` |
| `remove` | `sc.exe delete Tesserafin`, and nothing else |

The registration is [§4](W0-windows-server.md)'s service-contract table, not a
paraphrase of it: service name `Tesserafin`, display name `Tesserafin Server`,
the description `Tesserafin media server. Manage it at http://localhost:8096.`,
startup mode `Automatic (Delayed Start)` (`start= delayed-auto`), and the
recovery policy "restart after 60 s on first and second failure; no action on
the third, so a crash loop is visible rather than hidden"
(`reset= 86400 actions= restart/60000/restart/60000//0`). Control `M07` reads
those values back out of §4 itself, so a drift in the document fails the suite
rather than being silently outvoted by a constant.

`--webdir` and `--ffmpeg` are always passed explicitly, exactly as §4 requires
and exactly as the Linux unit does, "so the service can never silently fall back
to a `PATH` encoder or to a stale web directory". `--nowebclient` is never used.

`register` requires `-DataDir`, `-ConfigDir`, `-CacheDir` and `-LogDir`, refuses
a relative one, and refuses one that resolves inside the package directory. §6
says the ZIP "ships **no** state", and a state directory inside a tree that is
replaced wholesale on upgrade (§9.1) is state the next extraction destroys.

There is a second parameter set, `-Plan`, which resolves every path and builds
the exact `sc.exe` argv without contacting the Service Control Manager. It is
not a fifth verb: it is the dry evidence this slice is allowed to produce, and
the verbs and `-Plan` read the same `Get-ScInvocations` function, so the plan is
about the production call rather than beside it.

---

## 3. What it deliberately does not do

The script is a convenience over the §4 service contract and is **not a second installer**. Each of the following is a property §6 names, and each has a control behind it rather than a promise.

* **No repair.** `register` refuses a service that is already registered, before
  it calls `sc.exe` at all. Rewriting a registration in place is repair by
  another name. `remove` then `register` is the supported sequence, and it is
  two decisions by the operator rather than one guess by a script.
* **No rollback.** If `register` fails after `sc.exe create` has already
  succeeded, the script says exactly what exists and stops. It does not delete
  the service it just made. §6 calls rollback a property of the format "that no
  script can add", and a half-performed one leaves the operator with neither the
  service nor the certainty that there is no service.
* **No Add/Remove Programs entry.** Nothing writes
  `HKLM\…\CurrentVersion\Uninstall`, and no installer engine is invoked. A ZIP
  that advertised itself in Programs and Features would be claiming an uninstall
  contract it cannot honour.
* **No second copy of the server.** The service runs the `tesserafin.exe`
  already in the extracted directory. Nothing is copied to `%ProgramFiles%`, so
  there is no second tree that can drift from the one the operator extracted.
* **No baked install location.** There is no absolute path constant in the file.
  Control `M05` runs the same bytes from two directories at different depths and
  requires two different planned binary paths.
* **No machine-wide settings.** §4's 120 s stop timeout is
  `HKLM\SYSTEM\CurrentControlSet\Control\ServicesPipeTimeout`, which affects
  every service on the host, and §4's `NT SERVICE\Tesserafin` identity is only
  usable once §9's ACL grants exist on the state directories. Both are installer
  acts and both belong to W3's MSI. The script records them as deferred rather
  than registering an identity that could not reach its own data directory.
* **Nothing is fetched or evaluated.** No `Invoke-WebRequest`, no
  `Invoke-Expression`.

### The registration records a directory, and that is not a baked path

A registered service stores the `binPath` the operator's `register` produced.
Moving the extracted tree afterwards therefore needs `remove` and `register`
again. That is not a contradiction of §6's relocatability: the **archive** and
the **script** bake nothing, and the tree still starts from anywhere — which is
what W2-A3 proved, without the SCM. What is recorded is the SCM's own copy of a
decision the operator made at registration time.

---

## 4. Why an SCM start is not hosted evidence here

The ruling is explicit: *"Do not start the service on the runner (requires admin
/ SCM). Unit-level dry controls plus 'the file is in the ZIP at the named path'
are the evidence."*

There is also a measured reason. §4 records that the unmodified
`tesserafin.exe`, registered with the SCM and started, **fails with error 1053
after 7 seconds** — a plain console executable never calls
`StartServiceCtrlDispatcher`. §4 names that "a missing boundary in the
**server**" that "no installer technology can paper over", and closing it is
**W3**'s work, not this slice's. So `start` reports the SCM's verdict verbatim
rather than polling until something looks alive: a script that dressed 1053 up
as a slow start would be hiding the one fact W3 exists to fix.

A green hosted job on this pull request therefore says nothing about service
registration, and this document claims nothing from one. **An SCM start is not
hosted evidence, and none is presented.** W2-A3 already starts the executable
without the SCM, and that remains the only start proof W2 has.

---

## 5. The controls

`ci/windows/w2/service-script-controls.py`, 23 controls plus `ROSTER` and
`RESTORE`. Every refusal that can be reached without an SCM is driven through
the real script and paired with a live INERT-proof — a mutated copy with that
one check defeated, required to stop producing that one refusal.

| | |
| --- | --- |
| `M01` | four verbs, and a fifth is refused in the script's own words |
| `M02` | `register` requires all four state directories and defaults none |
| `M03` | a relative state directory is refused |
| `M04` | a state directory inside the package is refused |
| `M05` | the package is the script's own directory, and no location is baked in |
| `M06` | a script outside a complete package tree is refused |
| `M07` | the plan is W0 §4's service contract, and §4 still says so |
| `M08` | `--service`, the four directories, `--webdir` and `--ffmpeg` always; `--nowebclient` never |
| `M09` | `--webdir` and `--ffmpeg` point inside the package and nowhere else |
| `M10` | every SCM argument that can carry a space is quoted, and a quote is refused |
| `M11` | `-Plan` reaches no Service Control Manager and changes nothing |
| `M12` | the plan and the verbs read one definition of the SCM calls |
| `M13` | no repair: `register` refuses a service that already exists |
| `M14` | no rollback: a failed `register` undoes nothing and says so |
| `M15` | no Add/Remove Programs entry, no installer engine, no machine-wide write |
| `M16` | no second copy of the server, and nothing is fetched |
| `M17` | `remove` refuses a running service and deletes no file |
| `M18` | without Windows or without administrator the verbs fail closed |
| `M19` | the assembler stages the script at the frozen path and pins its digest |
| `M20` | the packed archive carries the script at that path, with the checkout's bytes |
| `M21` | `F18` still REDs any other new `.ps1` under `ci/windows/w2` |
| `M22` | the doc states the frozen path, the non-goals and claims no acceptance |
| `M23` | every accepted W2 file this slice may not change is unmodified |

`M13`, `M14` and `M17` are structural rather than behavioural, and deliberately
so: "no rollback" cannot be observed on a host with no SCM, because a test can
only fail to watch a service being deleted, which is indistinguishable from the
deletion existing and being unreachable on that input. They are asserted over
the PowerShell **AST**, clause by clause, over executable text only, so that the
script's own commentary about what it does not do cannot be mistaken for doing
it.

`M20` drives the frozen packer's own `-StageRoot` pack-only parameter set over a
synthetic stage and then opens the produced ZIP: the script must be a member at
`tesserafin-server_<version>_win-x64/tesserafin-server-service.ps1`, with the
checkout's exact bytes, under exactly one top-level directory. A stage without
the script must produce a listing without it, or the check is reporting nothing.

`M21` imports `w2_directory_findings` from `ffmpeg-consume-controls.py` rather
than restating the rule, so it is a statement about `F18` and not about this
file agreeing with itself.

### The `F18` amendment

`F18` in `ci/windows/w2/ffmpeg-consume-controls.py` calls any `.ps1` under
`ci/windows/w2/` a second FFmpeg consumer unless it is allowed by exact name.
It never opens the file, so a zero-byte placeholder is enough to RED. The W2-A5
ruling authorises the same one-line amendment W2-A2 and W2-A3 each needed: one
exact-name `continue` for `tesserafin-server-service.ps1`, and nothing else.
`F01`–`F17`, `F19`, the roster, the pins and the frozen-consumer byte pins are
untouched, and `F18` still REDs every other new `.ps1` — `M21` measures that
rather than assuming it.

---

## 6. Non-goals

This slice is deliberately narrow. It:

* **does not start the service.** No `sc.exe` runs on any runner, no service is
  registered, and no elevated session is used. An SCM start is not hosted
  evidence and none is claimed;
* **does not implement the `--service` boundary.** §4's error 1053 is unchanged
  at this master and closing it is W3's;
* **does not write the 120 s stop timeout or register the
  `NT SERVICE\Tesserafin` identity.** Both are machine-wide or ACL-dependent
  installer acts and belong to W3's MSI;
* renames no `.reefin` marker and changes no server C# file. The
  `.reefin` rename stays unauthorised and is **not done**;
* adds no workflow. The accepted two-runner and relocate-start workflows already
  watch `ci/windows/w2/**` and pick this change up by path filter;
* changes no `relocate-and-start.ps1` behaviour;
* does not **publish** anything: no package write, no release asset, no registry
  push, no tag;
* does not claim the MSI, the signing story or the acceptance matrix;
* edits previously accepted W2 files only where the ruling names them:
  `assemble-server-zip.ps1`, to stage the script at the frozen relative path and
  pin its bytes in the provenance manifest and for no other pack behaviour;
  `ffmpeg-consume-controls.py`, for the one `F18` exact-name `continue`; and
  `start-controls.py` and `two-runner-controls.py` for **pin values only**,
  because `S15` and `T15` pin the files the first two edits change.
  `consume-web-payload.ps1`, `relocate-and-start.ps1`, `pkg-tree-digest.py`,
  `zip-controls.py`, `web-payload-controls.py`, the runtime-retention consumer
  and the acceptance manifest are all untouched, and `M23` pins them.

**W2 is not accepted by this slice.** Independent review is the next gate.
