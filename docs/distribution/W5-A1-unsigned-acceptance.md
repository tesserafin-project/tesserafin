# W5-A1 — unsigned `win-x64` portable ZIP acceptance

Tracker: [#276](https://github.com/tesserafin-project/tesserafin/issues/276).
Ruling: **OWNER RULING — W5-A1 UNSIGNED ZIP ACCEPTANCE**, authorising this slice
from master `f90fd18862a79d4f08cb4a52223bc852a3ffd541` (W5-A0 as accepted).
Contract: [`W5-A0-acceptance-contract.md`](W5-A0-acceptance-contract.md).
Refs [#129](https://github.com/tesserafin-project/tesserafin/issues/129),
[#256](https://github.com/tesserafin-project/tesserafin/issues/256) (historical W2).

W5-A1 is the unsigned acceptance harness. It measures
`tesserafin-server_1.0.0_win-x64.zip` as a function of the commit built and the
frozen W1 FFmpeg / W2 Web pins. It does **not** recover the W2 closeout ZIP
digest, which W5-A0 records as UNKNOWN and which stays UNKNOWN. It does not
adopt `7a6393f6…`, the W2-A5 witness, as anything. W5 is not accepted by this
document.

---

## 1. The measured digest

| field | value |
| --- | --- |
| `zipSha256` | **TO BE MEASURED** |
| `serverCommit` | TO BE MEASURED |
| `webPayloadSha256` | `4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f` |
| `ffmpegRuntimeSha256` | `f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e` |
| `sourceDateEpoch` | TO BE MEASURED |
| `evidenceRunId` | TO BE MEASURED |
| allocation A / B SHA-256 | TO BE MEASURED |

A digest written before the hosted pair is terminal is a guess. This table and
`ci/windows/w5/accepted-unsigned-zip.json` are filled in by one later commit,
and only after a PASS of `W5 unsigned win-x64 ZIP acceptance` at a named head.

## 2. What it proves

Three facts, and only these.

1. **Two allocations, one digest.** `assemble-a` and `assemble-b` run on two
   `windows-latest` allocations. Each `needs:` only `prepare`, never the other,
   uses no cache and runs the frozen `ci/windows/w2/assemble-server-zip.ps1`,
   which drives the frozen `consume-web-payload.ps1`, `runtime-retention/consume.ps1`
   and `pkg-tree-digest.py`. `SOURCE_DATE_EPOCH` is derived **once**, in
   `prepare`, from `git log -1 --format=%ct <head>`, and passed identically to
   both. Each uploads its ZIP and nothing else — no hash file, no job output —
   retained 14 days.

2. **A verify job that built nothing.** `verify` has no SDK, no `dotnet`, no
   assembler. It downloads the two archives, and
   `ci/windows/w5/verify-unsigned-zip.py` accepts only if:
   * the two SHA-256 values **it computes from the downloaded bytes** are equal;
   * the unpacked `web/` tree hashes, under the frozen `pkg-tree-digest.py`, to
     `4148c4bc…`;
   * the FFmpeg runtime archive the verify job acquires **for itself**, through
     the frozen W1 consumer, hashes to `f28cc918…`, and the ZIP's `ffmpeg/` tree
     holds exactly that archive's members, byte for byte;
   * `licenses/provenance.json` names `serverCommit` = the commit built, its
     `sourceDateEpoch`, the Web pin and the FFmpeg pin;
   * `tesserafin.exe` is PE x64 (machine `0x8664`), `hostfxr.dll`,
     `hostpolicy.dll`, `coreclr.dll` and `System.Private.CoreLib.dll` are at the
     package root, and `tesserafin.runtimeconfig.json` declares
     `includedFrameworks` and neither `framework` nor `frameworks`.

   The inner FFmpeg archive is not itself a member of the ZIP — the assembler
   extracts it into `ffmpeg/` — so "the inner archive equals `f28cc918…`" is
   measured as: the accepted archive, independently acquired and hashed, has
   exactly the member set and member bytes the ZIP carries.

   `verify` is `if: always()` over `controls`, `prepare` and both assemble jobs,
   and fails first if any of them did not succeed, so a missing pair is RED and
   never skipped.

3. **Pin only after a terminal PASS.** Nothing in this commit names a ZIP digest.

It does not start `tesserafin.exe` (W2-A3 evidence), register a service, build
or hash an MSI (W4 made no bit-identical MSI claim), sign, tag, release or push
to GHCR. `SharedVersion` stays `1.0.0`.

## 3. Path filter — the W2 residual, closed

W2-A6 and W2-A7 changed server C# that ships in the ZIP and never queued the W2
two-runner workflow. `w5-unsigned-zip.yml` watches every tree compiled or packed
into the archive: the ruling's minimum list, plus every project in the
`ProjectReference` closure of `Tesserafin.Server.csproj` (14 top-level projects
and `src/**`), `Directory.Build.props`, `Directory.Packages.props`,
`global.json`, `nuget.config`, the analyzer inputs, `LICENSE`, and the pinned
ORAS installer and lock. Control `W00` recomputes the closure from the csproj
graph on every run, so a new `ProjectReference` outside the filter is RED.

## 4. Hostile controls

`ci/windows/w5/unsigned-zip-controls.py`, run by the `controls` job. Every
verifier control is observed RED against the real verifier and then against a
mutant with that one check removed, where it must disappear; every workflow
control mutates a copy of the workflow text and must be named by the audit. A
mutation that no longer applies reports INERT, which is a failure.

| id | hostile condition | observed |
| --- | --- | --- |
| V01 | assemble A and assemble B differ | `HASH-MISMATCH` |
| V02 | verify trusts an assemble-job hash (a lying `.sha256` sidecar; mutant reads it instead of hashing) | `HASH-MISMATCH` from the real verifier, PASS from the mutant |
| V03 | Web tree digest ≠ `4148c4bc…` | `WEB-DIGEST` |
| V04 | FFmpeg runtime archive ≠ `f28cc918…` | `FFMPEG-ARCHIVE` |
| V05 | ZIP `ffmpeg/` tree ≠ the accepted archive's members | `FFMPEG-TREE` |
| V06 | `provenance.json` `serverCommit` ≠ head | `PROVENANCE-COMMIT` |
| V07 | `tesserafin.exe` not PE x64 | `PE-X64` |
| V08 / V09 | shared framework declared / `coreclr.dll` absent | `SELF-CONTAINED` |
| V10 | verifier names a publish, the assembler, a hash file, or takes a pin argument | static |
| W01 / W02 | `permissions: write-all` / a job with `contents: write` | `PERMISSIONS` |
| W03 / W04 | `pull_request_target` / `push` trigger | `TRIGGERS` |
| W05 / W06 | verify job calls `dotnet publish` / `assemble-server-zip.ps1` | `VERIFY-BUILDS` |
| W07 | verify job reads `needs.assemble-a.outputs.sha256` | `VERIFY-TRUSTS` |
| W08 | assemble B `needs:` assemble A | `ALLOCATION` |
| W09 | an assemble job uploads a hash file instead of the ZIP | `UPLOAD` |
| W10 | verify job loses `if: always()` | `VERIFY-SKIPPABLE` |
| W11 | artifact retention under 14 days | `RETENTION` |
| W12 / W13 | path filter omits `Tesserafin.Server.Core/**`; a one-line C# edit is planted in a copy of a real Core file | `PATHS`; the plant queues with the committed filter and not without that line |
| P01 | a frozen W2 input, W0–W4 workflow, `SharedVersion.cs` or `Tesserafin.wxs` changed, or a file added under `ci/windows/w2/` | byte pins at `f90fd18862` |
| P02 | the verifier's pins disagree with `ci/package/pins.env` / `accepted-runtime.json` | pin equality |

## 5. Disclosed deviations

* **`packages: read` on the two assemble jobs.** The workflow's permissions are
  exactly `contents: read`, `actions: read`, `pull-requests: none`. The frozen
  W2-A0 Web consumer refuses an anonymous pull and may not be edited, so each
  assemble job carries a job-scoped `packages: read` and nothing else. No other
  job holds it; `W00` asserts that.
* **The Core plant is evaluated, not pushed.** W12/W13 evaluate the committed
  `paths:` filter, with GitHub's `*` / `**` semantics, against a real one-line
  edit planted in a temporary copy of
  `Tesserafin.Server.Core/AppBase/BaseApplicationPaths.cs`. A hosted queue
  observation would need a commit touching `Tesserafin.Server.Core/`, which is
  outside this slice's authorised paths.
* **The verify job runs the frozen W1 consumer.** It is how the job acquires the
  accepted FFmpeg runtime archive without trusting the assemble jobs. It pulls
  anonymously, with an empty `DOCKER_CONFIG`, and builds nothing.

## 6. Not claimed

No signature, no MSI digest, no 1.1 version number, no W5 acceptance, no W5-A2.
Independent review is the next gate after the pin commit exists.
