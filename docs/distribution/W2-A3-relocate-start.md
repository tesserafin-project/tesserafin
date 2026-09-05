# W2-A3 — Starting the win-x64 server ZIP after relocation

Tracker: [#256](https://github.com/tesserafin-project/tesserafin/issues/256).
Umbrella: #234. Base master:
`5ba584383c7381bea1c23708a2e3781bb428326b`, which is W2-A2 as accepted.

This slice proves one thing on one `windows-latest` runner:

> the tree produced by the **frozen** W2-A2 assembler starts after a
> hostile-path relocation, and starts again after a second move to a different
> depth.

It is one slice of W2. **W2 is not accepted by this document.**

---

## 1. What was proved

[W0 §6](W0-windows-server.md) states the property this slice discharges, in its
own words:

> **Relocatable.** Proven by moving the tree and starting it again (§2.3); the
> server must bake its own location into nothing.

and, in the same section:

> Ships **no** state. Configuration, database, cache and logs are always given
> by argument.

`ci/windows/w2/relocate-and-start.ps1` proves exactly that:

1. it calls the **frozen** `ci/windows/w2/assemble-server-zip.ps1` in-job, which
   drives the frozen W2-A0 Web consumer and the frozen W1/W2-A1 FFmpeg consumer;
2. it extracts the resulting archive **once**, into a directory whose name
   carries spaces, accented Latin, an em dash and CJK;
3. it starts that tree with fresh isolated `--datadir --configdir --cachedir
   --logdir`, and with `--webdir` and `--ffmpeg` pointing **inside** the
   relocated tree;
4. it discovers the listening port from the process, waits for `/` to answer
   something other than `503`, requires the process to still be running three
   seconds later, and requires the entry document to reference the hashed
   `main.tesserafin…bundle.js`;
5. it stops the server, **moves the same tree** to a different depth, gives it
   state directories that share nothing with the first start, and repeats every
   check.

### 1.1 The two hosted starts

The first hosted `windows-latest` run for this branch is the evidence. Both
discovered ports and both readiness status codes are printed to the job log and
to the run summary by the workflow's `Report both starts` step, and are also
carried in the evidence document that step prints whole.

**Cited in the pull request**, because this document is committed before the
hosted run exists and a document that quoted numbers it could not have had would
be a document nobody should trust. The shape of what is reported is fixed here:

| | start 1 (hostile path) | start 2 (relocated, deeper) |
| --- | --- | --- |
| discovered port | from the process | from the process, and different |
| `/` status | not `503` | not `503` |
| liveness after the answer | 3 s | 3 s |
| `web/index.html` | `200` + hashed bundle | `200` + hashed bundle |

The two discovered ports are required to **differ**. Each start seeds its own
`network.xml` with a distinct free port, so two starts that reported the same
port could not both have read it from their own process, and the proof refuses
that pair.

### 1.2 The hostile path

The first directory leaf is built from code points rather than written as
literals, so the characters actually under test are stated in the source and
cannot be silently normalised by an editor, a checkout or a console code page:

| what | code point |
| --- | --- |
| accented Latin | `U+00E9` |
| em dash | `U+2014` |
| CJK | `U+76EE U+5F55` |
| spaces | three of them |

W0's probe builds its leaf the same way, for the same reason. These fail
*differently* — quoting, the console code page, and a code point outside any
single-byte page — so a proof that exercised only one of them would prove a
quarter of the thing.

### 1.3 "The same tree", measured rather than asserted

| claim | how it is measured |
| --- | --- |
| extracted once | the archive is opened exactly once and the count is asserted |
| moved, not re-extracted | `Move-Item`, and the extraction count must still be 1 |
| the same bytes | `tesserafin.exe` is hashed on both sides of the move |
| the first location is gone | asserted after the move |
| a different depth | the two path depths must differ |
| not the build tree | both roots are asserted to be outside the assembler's own work and output directories |
| the running image is the relocated one | the process's own `MainModule.FileName` must equal the launched path |

---

## 2. The four W0 §2.3 readiness traps

W0 walked into all four. Each one made a **failing** server look like a starting
one, or the reverse, and W2's acceptance suite inherits every one of them. They
are implemented in `relocate-and-start.ps1`, not restated.

### 2.1 The port is not in configuration or the environment

`ApplicationHost` reads `NetworkConfiguration.InternalHttpPort`, persisted as
`<configdir>/network.xml`, and the server logs only `Kestrel is listening on
0.0.0.0` with no port in it. W0 §2.3 puts the consequence plainly:

> Seeding that file is a request rather than a guarantee […] so the probe asks
> the process itself which TCP ports it bound. Without that, a run where the
> server logged `Startup complete` in 22 s was still recorded as "did not
> start" […] and **W2's acceptance suite must discover the port the same way.**

So the proof seeds `network.xml` with a distinct free port per start — a
*request*, and the only way to keep the two starts off one socket — and then
reads the bound port from `Get-NetTCPConnection -OwningProcess`. The base URL is
built from the discovered value and from nothing else.

**When the process bound nothing, the proof refuses.** There is deliberately no
fallback to the file, to the environment or to the compiled-in default: a
fallback is indistinguishable from never having asked the process, and it turns
"the server did not start" into a green run against a port nothing is listening
on. `start-controls.py` S07 asserts the literal `8096` appears nowhere in the
executable text of the production path, that no URL is built from the requested
value, and that `Resolve-ServerPort` returns no literal port.

In the real run the requested and discovered ports coincide by construction, so
the file-versus-process preference is not observable there. It is observed in
the control suite instead, against a fixture where the two disagree — S05.

### 2.2 A redirect is an answer

`Invoke-WebRequest -MaximumRedirection 0` raises on a 3xx, a class
`-SkipHttpErrorCheck` does not cover, and `/` redirects to the web client — so a
healthy server looked identical to one that was not answering at all. That cost
W0 two hosted runs. The proof uses `HttpClient` with `AllowAutoRedirect`
disabled, which returns the 302 as a value.

### 2.3 A 503 from the SetupServer is not ready

The startup `SetupServer` binds the real port early and answers **every** path
with `503` and `{"status":"starting",…}`. Readiness is "`/` answers with
something other than `503`". Requiring `200` would be wrong in the other
direction: that asserts a routing decision rather than liveness, and S03
observes that a 200-requirement calls a healthy redirecting server dead.

This is also why readiness is keyed to `/` and never to `/System/Info/Public`:
the latter is served by the setup server long before the application host is up.

### 2.4 A live process is part of readiness

Even the 503 rule was not enough. W0 records that the setup server answered a
non-`503` in the same instant the application host was tearing itself down over
`FfmpegException`, and that the no-FFmpeg negative control **reported a started
server** while its own log showed the host disposing. Readiness therefore
requires the process to still be running three seconds after it answers.

---

## 3. The hostile controls

`ci/windows/w2/start-controls.py`. Nineteen rows: S01–S17, plus `ROSTER` and
`RESTORE`. The suite exits non-zero on any RED **or** any INERT.

The readiness, port and Web-bootstrap rules are driven against real HTTP
fixtures through the production script's own `-Oracle` parameter set — the same
functions the hosted run uses, not a second copy written for a test — and each
is paired with a mutated copy of the script carrying that one check defeated.

| control | what it observes |
| --- | --- |
| S01 | a server that only ever answers 503 is not ready |
| S02 | readiness is keyed to `/`, never to `/System/Info/Public` |
| S03 | a redirect is an answer, and requiring 200 is a different rule |
| S04 | the process must still be running three seconds after it answers |
| S05 | the port comes from the process, and an empty answer is refused |
| S06 | the entry document must reference the hashed bundle |
| S07 | the port is never read from configuration, the environment or a default |
| S08 | the two starts are given their own state, and a shared one is refused |
| S09 | a start from the assembler's own tree is refused |
| S10 | the archive is extracted once and the second start is a move |
| S11 | no caller-supplied identity anywhere on the production path |
| S12 | the ruling, the proof, the acceptance manifest and the workflow agree |
| S13 | no Actions artifact, cache or dispatch, and the oracle is not the production door |
| S14 | the new workflow is the authorised one, byte for byte |
| S15 | the five frozen inputs are unmodified |
| S16 | the proof registers no service, publishes nothing and starts no third time |
| S17 | the workflow watches the whole `ci/windows/w2` tree (W2-A1-V1 NB-1) |

`ROSTER` turns a deleted or renamed control into a RED rather than into a
shorter green summary line, and `RESTORE` asserts that the controls modified no
audited file rather than assuming it.

### 3.1 Observed, not assumed

Each named refusal was observed RED against a disposable copy of the proof:

| planted defect | control | observed |
| --- | --- | --- |
| readiness accepts any status | S01 | RED, `ready status=503` |
| readiness requires 200 | S03 | RED, the 302 fixture read as never ready |
| readiness has no liveness gate | S04 | RED, `answered 302 but process … was gone within 3s` |
| readiness keyed to `/System/Info/Public` | S02 | RED, named in the audit |
| the port falls back to the default | S05 | RED, `ORACLE port: 8096` |
| any 200 is the Web client | S06 | RED, `ORACLE bundle: present` |
| the V1 workflow shape | S14 | RED, with named findings |
| one audit deleted | ROSTER | RED, suite exit 1 |

### 3.2 Three defects the controls found in themselves

Recorded because a control suite that quietly repairs itself is a suite nobody
can grade:

* the `answer-then-die` fixture became a **zombie** because nothing reaped it,
  so a POSIX liveness check three seconds later still found it alive and graded
  the real rule RED. Fixtures are reaped by a daemon thread now. Windows has no
  zombies and would not have shown this;
* S11 sliced the oracle's text at a `# ===` banner, but `executable_text` blanks
  comments, so the split never matched and the slice swallowed the whole
  production block — reporting that the oracle assembles. It terminates at the
  production block's own `try {` instead;
* `expect_oracle` checked whether a mutation still applied **before** grading
  the real decision. Planting the defeated checks in the production path then
  graded S01, S04 and S06 `INERT` — a harness complaint — for exactly the
  defects those controls exist to name. The real decision is graded first now.

---

## 4. The workflow

`.github/workflows/w2-windows-relocate-start.yml`.

| property | value |
| --- | --- |
| trigger | `pull_request` only, drafts included |
| paths | the whole `ci/windows/w2/**` tree, this workflow, this document, the two frozen runtime-retention files and `SharedVersion.cs` |
| permissions | `contents: read`; `packages: read` scoped to the job, for the Web pull |
| runner | `windows-latest`, one job, `timeout-minutes: 150` |
| checkout | `persist-credentials: false`, both actions SHA-pinned |
| artifacts | none uploaded, none downloaded, no cache |
| container engine | none named |
| assemblies | one |
| starts | two, inside one invocation of the proof |

The `paths:` filter watches the authored **directory** rather than a list of
names. That is the W2-A1-V1 NB-1 repair W2-A2's workflow already made: a file
added under `ci/windows/w2/` must not be reviewable without running the proof
that covers it.

The workflow is pinned raw-byte by S14, which also drives the W2-A0-V1
permission mutation against the audit and requires named findings rather than a
bare hash mismatch.

---

## 5. W2-A3-F18

A1's `F18` reports every `.ps1` under `ci/windows/w2/` that is not on its
allowlist as "a second consumer". It reads a directory **listing** and never the
file, so the authorised path `ci/windows/w2/relocate-and-start.ps1` reddened it
as a zero-byte placeholder. Measured on the frozen master before any change:
A1 21 PASS / 1 RED.

The owner amendment W2-A3-F18 authorises one edit, the same one W2-A2-F18
already made for `assemble-server-zip.ps1`: one exact name, allowed as a
sibling. `F18` is **not** widened to "any `.ps1` under `w2/`", it is not deleted,
and `F01`–`F17`, `F19`, `ROSTER` and the frozen byte pins are untouched. `F18`
still reports a disposable `ci/windows/w2/ffmpeg-consume.ps1` as a second
consumer, which was observed rather than assumed.

The start proof is not an FFmpeg consumer. It acquires nothing: it calls the
frozen assembler, which is the only thing on this path that drives
`ci/windows/runtime-retention/consume.ps1`.

The authorised path inventory for this slice is therefore five:

```
ci/windows/w2/relocate-and-start.ps1
ci/windows/w2/start-controls.py
ci/windows/w2/ffmpeg-consume-controls.py      (the one-line F18 allowlist)
.github/workflows/w2-windows-relocate-start.yml
docs/distribution/W2-A3-relocate-start.md
```

---

## 6. Non-goals

Stated explicitly, because each is a claim a reader could otherwise infer from a
green run:

* **No §8.1 two-runner ZIP identity.** This job assembles **once**, on **one**
  runner. W2-A2 made the one-runner determinism claim and said so in its own
  words; nothing here strengthens it, and two runners remain unproven.
* **No first-party service script.** §6's last bullet — the PowerShell script
  that registers, starts, stops and removes the service — stays deferred, and W4
  owns the SCM. Nothing here registers a service or passes `--service`.
* **No `.reefin` rename.** `BaseApplicationPaths.MakeSanityCheckOrThrow` still
  writes `.reefin-config`, `.reefin-log` and their siblings. W0 §2.3 records that
  W2 should rename them *with a migration that recognises the old marker*; that
  is not this slice, and no server C# file changes here.
* **No publication.** No release, no package write, no registry push, no Actions
  artifact. W0 §8.7 forbids the artifact handover in production and this slice
  does not use one: the archive under test is assembled in the same job that
  starts it.
* **No third start.** The pair is the proof.
* **No repair of A2's NB-1 or NB-2.** Both are retained.
* **`/health` is not a readiness gate here.** W0 measured that it lags `/`; that
  lag is a contract detail W4 needs and this slice neither re-measures nor
  depends on it.
* **Shutdown is not measured.** W0 measured that a console process started with
  `-NoNewWindow` has no main window, so `CloseMainWindow` is a no-op and every
  stop degrades into a kill. The first server is killed and waited for, which is
  what the second start needs and all it needs.
* **W2 is not accepted.** This is one slice. Independent review is the next gate.
