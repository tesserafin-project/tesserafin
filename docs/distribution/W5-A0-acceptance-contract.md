# W5-A0 — the acceptance contract for Tesserafin 1.1

Tracker: [#276](https://github.com/tesserafin-project/tesserafin/issues/276).
Ruling: **OWNER RULING — W5-A0 ACCEPTANCE CONTRACT**, authorising this document
from master `331b220522d7acfa2eb62447640af2091f7949a9` (W4-CLOSEOUT).
Refs [#129](https://github.com/tesserafin-project/tesserafin/issues/129),
[#234](https://github.com/tesserafin-project/tesserafin/issues/234) (historical),
[#225](https://github.com/tesserafin-project/tesserafin/issues/225).

This document decides. It does not implement. It creates no certificate, no key,
no secret, no signing job, no Git tag, no GitHub Release, no GHCR publication and
no `SharedVersion` bump.

---

## A. Frozen inputs

Every value below is recomputed from the object graph at
`331b220522d7acfa2eb62447640af2091f7949a9` — `git show` of the accepted document
or pinned file named in `Recorded in`, `git merge-base --is-ancestor` of every
commit against that master, and each pull request's recorded merge commit
compared against its own head — not copied from any pull request body.

| Input | Value | Recorded in |
| --- | --- | --- |
| W1 accepted FFmpeg runtime — OCI manifest digest | `sha256:99e45f154a5d72aba4185eb19b6671aa1a11c30be837deac9dd26f473593c0b9` | [`W1-windows-runtime-retention.md`](W1-windows-runtime-retention.md) §3; `ci/windows/runtime-retention/accepted-runtime.json` `manifestDigest` |
| W1 accepted FFmpeg runtime — runtime archive SHA-256 | `f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e` | same document §3; `accepted-runtime.json` `runtimeSha256` |
| W1 accepted FFmpeg runtime — corresponding source SHA-256 | `d753268c14d8e312bdd8ccd5ce8af90d495d185e807b77621e229eb8f71cc76d` (decompressed stream `5158221a246c7e7d0d843d649571625ad0277152a093361419414195a8afee8e`) | same document §3; `accepted-runtime.json` `correspondingSourceSha256`, `correspondingSourceStreamSha256` |
| W2 accepted portable ZIP digest | **UNKNOWN** | see below |
| W2 accepted Web payload — canonical tree digest | `4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f` | [`W2-A0-web-payload.md`](W2-A0-web-payload.md), "The accepted contract"; `ci/package/pins.env` `WEB_PAYLOAD_SHA256` |
| W2 accepted Web payload — image manifest digest | `ghcr.io/tesserafin-project/tesserafin-web-assets@sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c` (Web revision `a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a`) | [`W2-A0-web-payload.md`](W2-A0-web-payload.md), "The accepted contract" |
| W3 accepted service-host head | `fd8187f15ed7aa99552971305d5ac3f414c4d958` — W3-A1 as accepted, [#267](https://github.com/tesserafin-project/tesserafin/pull/267) | [`W4-accepted.md`](W4-accepted.md) header, "Frozen W4 starting master"; [`W3-A1-preconfig-failure.md`](W3-A1-preconfig-failure.md) |
| W4 accepted MSI surface head | `295c5f2792ff2422e082b386b8c138674029d0b7` — W4-A6 as accepted, [#274](https://github.com/tesserafin-project/tesserafin/pull/274) | [`W4-accepted.md`](W4-accepted.md) §1 and §4 |
| W4 closeout head | `331b220522d7acfa2eb62447640af2091f7949a9` — W4-CLOSEOUT, parent `295c5f2792ff2422e082b386b8c138674029d0b7` | [`W4-accepted.md`](W4-accepted.md) is the document this commit adds |
| MSI `UpgradeCode` | `0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05` | [`W4-A1-upgradecode.md`](W4-A1-upgradecode.md); `ci/windows/w4/msi-controls.py` `FROZEN_UPGRADE_CODE` |

### Notes on the table

* **W3 has no closeout document.** There is no `W3-accepted.md` in
  `docs/distribution/`. The W3 head above is the one `W4-accepted.md` names as
  W4's frozen starting master. W3-A0 as accepted is
  `e463d92650efc2c0ebfc15d6d321db543954f361`
  ([#266](https://github.com/tesserafin-project/tesserafin/pull/266)); each pull
  request's merge commit equals its head.
* **The W2 portable ZIP digest is UNKNOWN, and this document stops there.**
  - No accepted document records it. `W2-A2-server-zip.md` states that no
    archive digest is written into this repository and that the hosted hashes
    are cited in the pull request; `W2-accepted.md` records slice SHAs only.
  - No retained artifact carries it for the W2 accepted head
    `fb43b1627f1f77abf88777344670c33a7464ecbd`. The last
    `W2 Windows two-runner ZIP identity` run is `34199702764`, at W2-A5's head
    `147687f323cdc77f545520a095abf2f7c2a5df1e`. No run of that workflow exists at
    W2-A6 (`e572db1f7f…`) or W2-A7 (`fb43b1627f…`), whose server C# changes are
    outside its path filter (`W2-accepted.md` §3, A3).
  - That run's retained evidence (`w2a4-evidence-a`, `w2a4-evidence-b`, expiring
    2026-09-15) reports one SHA-256 on both allocations,
    `7a6393f66f490d70d7e05e27764f2357dfb335b6d72590aa3b01983d6c1324c0`. It is the
    A5-head ZIP. It is **not** the W2 accepted ZIP digest and is not adopted as
    one: A6 and A7 change `BaseApplicationPaths.cs` and `BackupService.cs`, which
    are compiled into the ZIP.

  Recovering or re-measuring a W2 ZIP digest is not authorised by W5-A0.

---

## B. What Tesserafin 1.1 is

Deliverables, not intentions:

* **Linux container** — the existing 1.0 path, still digest-pinned.
* **Native Linux packages** — `.deb`, `.rpm` and portable `.tar.gz` for
  `linux-x64` and `linux-arm64`, as accepted in
  [#225](https://github.com/tesserafin-project/tesserafin/issues/225) /
  [`L0-linux-packages.md`](L0-linux-packages.md).
* **Native `win-x64` portable ZIP** — W2.
* **Native `win-x64` MSI and Windows Service** — W3 and W4.
* **Bundled Tesserafin Web**, digest-pinned (§A).
* **Tesserafin-owned FFmpeg runtime**, digest-pinned (§A).

The interface on Windows, macOS and Linux remains Tesserafin Web in a browser.
There is no native desktop client.

---

## C. What Tesserafin 1.1 is not

Copied from [`W4-accepted.md`](W4-accepted.md) §2:

* **A bit-identical MSI.** No slice built the package twice and compared bytes.
  W4 makes no reproducibility claim about the MSI itself; the two-clean-build
  proof W0 requires is the FFmpeg runtime's, not the installer's.
* **Signing and Authenticode.** No certificate, no key, no timestamp, no signed
  artifact. The explicit release-signing decision W0 requires is unmade.
* **Advertised repair.** The lifecycle W4 measured is install, upgrade and
  uninstall. Repair semantics are unexercised.
* **Application and media Event Log events.** A6 registered the source and
  measured service-lifecycle events through it. No application event and no
  media event is written or read by any slice.
* **`win-arm64`.** Nothing here is built, installed or measured for
  `win-arm64`, and no release promise is made for it.

Added by this contract:

* **No Authenticode signature yet.** The decision stands (§D); the
  infrastructure is W5-A1 onward, not A0.
* **No advertised MSI repair proof.**
* **No `win-arm64`.**
* **No D3D11VA, DXVA2, QSV, NVENC or AMF runtime claim.** Hosted runners have no
  GPU; compiled capability is not a runtime claim.
* **No Secure Remote Access or managed HTTPS.**
  [#241](https://github.com/tesserafin-project/tesserafin/issues/241) is
  post-1.1.
* **No native client.**
* **No Jellyfin client or plugin compatibility.**
* **No auto-updater.**
* **`CHANGELOG.md` still describes 1.0.0**, and still says "Only the Linux
  container is a supported deployment surface." That sentence becomes false in
  W5-A3, not here.

---

## D. Signing decision — restated, not reopened

[`W0-windows-server.md`](W0-windows-server.md) §11 and §8 ("Signing is a later
transformation") stand:

* 1.1 ships an Authenticode-signed MSI and signed first-party executables.
* SHA-256 digests and an RFC 3161 SHA-256 timestamp.
* An organisation-validated certificate held in a hardware token or a cloud
  signing service.
* The private key is never a file in the repository, never an Actions secret of
  type "paste the PFX", and never on a developer machine.
* The signing job is reachable only from `master` — never from a fork, never from
  `pull_request_target`.
* Reproducibility is proven on unsigned bytes. The signature is a later
  transformation. The signed artifact must name the accepted unsigned digest.
* Unsigned artifacts remain usable.

### Vendor: UNDECIDED

W5-A0 does not choose between DigiCert, Sectigo, SSL.com, Azure Trusted Signing
or SignPath. Whichever is chosen must satisfy every custody constraint:

1. The certificate is organisation-validated (or the service's equivalent
   organisation identity) and valid for Authenticode code signing.
2. The private key is non-exportable: generated and held in a hardware token or
   an HSM-backed cloud signing service, never delivered or stored as a PFX or any
   other file.
3. No Actions secret contains key material. Any credential the signing job holds
   authorises a signing request and cannot reconstruct the key.
4. Signing can be invoked only from a job reachable from `master`: no fork, no
   `pull_request`, no `pull_request_target`.
5. It produces SHA-256 file digests and an RFC 3161 SHA-256 timestamp from a
   public timestamp authority.
6. It signs both MSI packages and PE executables.
7. The result verifies with `signtool verify /pa /all`.
8. It does not require signing to happen before reproducibility acceptance, and
   does not require the unsigned artifact to change: signing is applied to the
   accepted unsigned bytes, and the signed artifact can name their digest.

---

## E. W5 decomposition — frozen by this document

| Slice | Scope |
| --- | --- |
| **W5-A0** | this contract |
| **W5-A1** | unsigned acceptance harness (reproducibility + provenance verification on evidence no build job produced; no cert) |
| **W5-A2** | signing job skeleton: fail-closed without a cert; verify path; no production signature |
| **W5-A3** | 1.1 documentation integration: CHANGELOG 1.1 section, versioning-policy publication rules for 1.1, in-repo install docs for ZIP + MSI. No website deploy. |
| **W5-A4** | owner-only publication: tag, GitHub Release, GHCR copy-by-digest, tesserafin.org download page. A4 is not agent-authorizable from this ruling. |

---

## F. Residuals carried forward, not repaired

Copied from [`W4-accepted.md`](W4-accepted.md) §3 as-is.

### A3 — the inert workflow guard and three unreddenable predicates

**NB3** — the one-line workflow guard is inert. `appliedAcls` is the hardcoded
literal `$true` at `probe-msi-skeleton.ps1:156`, so
`if (-not $evidence.appliedAcls) { throw … }` can never fire, and reverting the
line on a disposable copy left `msi-controls.py` clean — the guard is not
statically covered. It is not load-bearing and not a false-green hazard: the
ACLs are graded by eleven `Get-Acl` predicates, and `msi-controls.py:344`
independently requires a `PermissionEx` element to exist.

**O1** — three predicates no declared control reddens:
`installFolderServiceCanReadAndExecute`, `installFolderUsersHaveNoWrite` and
`dataRootInheritanceBroken`. Only the third is disclosed in-tree. All three are
falsifiable at the predicate level and the review reddened each of them with its
own plants, so they are load-bearing — just not mutant-covered.

### A4 — no remember-property, **closed by A5**

**NB-3** on #272 was that the authoring had no remember-property on
`INSTALLFOLDER`: the probe passed it explicitly to both installs and a static
control refused a probe that stopped doing so. This residual is **closed** by
W4-A5 (#273), which authored the property — `SetProperty` conditioned on
`REMEMBEREDINSTALLFOLDER AND NOT INSTALLFOLDER` — and measured the omission on
a real upgrade pair whose B install carried nothing on its command line. It is
listed here because the closeout ruling names it, and it is listed as closed.

### A5 — `upgradeOmittedInstallFolder` is a literal

`upgradeOmittedInstallFolder` is a runtime constant, a literal `$false`, while
the `psm1` comment and §1 of `W4-A5-remember-installfolder.md` both describe it
as a measurement. The fact it stands for is proved elsewhere — by the static
gate and by the nine declared rows of the `upgrade-no-remember` control — so the
misdescription did not block acceptance. It is unrepaired.

### A6 — the inert regex, the tautological log name, the unmeasured rethrow

* The `EventLog\\(?!Application\\)` static gate is **inert**: the backslashes
  are doubled, so the pattern cannot match the registry paths it is meant to
  reject. The A6 document and the #274 body overstate it as asserted and
  self-tested.
* `eventLogSourceLogIsApplication` is **tautological**: the instrument returns
  the log name it was passed, so the predicate cannot fail.
* The reader's **rethrow arm is unmeasured**. The R1 fix makes an unregistered
  provider read as "no events" rather than a throw; the arm that rethrows any
  other failure is not exercised by any control.

### `INSTALLFOLDER` remember is A5's work, not a residual

To be unambiguous, because §3 above mentions it twice: remembering
`INSTALLFOLDER` across a `MajorUpgrade` is **delivered and accepted** work —
W4-A5, #273, accepted head `9f351f594d7abfb0bb4420acea16a96698afc48f`. It is
not a residual of W4. The only A5 residual named here is the
`upgradeOmittedInstallFolder` literal.

### Everything else each slice retained

Each slice's remaining non-blocking findings and observations stand where its
review left them — in that slice's pull request body and document — unrepaired
by this closeout.
