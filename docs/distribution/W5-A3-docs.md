# W5-A3 — 1.1 documentation integration

Tracker: [#276](https://github.com/tesserafin-project/tesserafin/issues/276).
Ruling: **OWNER RULING — W5-A3 1.1 DOCUMENTATION**, authorising this slice from
master `5f6378ecb177031649ff51b523bdf8cfcafaaa76` (W5-A1 as accepted).
Contract: [`W5-A0-acceptance-contract.md`](W5-A0-acceptance-contract.md) §B, §C, §E.
Refs [#129](https://github.com/tesserafin-project/tesserafin/issues/129),
[#225](https://github.com/tesserafin-project/tesserafin/issues/225).

W5-A3 is documentation only. It signs nothing, tags nothing, releases nothing,
publishes nothing to GHCR, deploys nothing to tesserafin.org, does not bump
`SharedVersion`, and does not open W5-A2 or W5-A4.

---

## 1. What changed

| Path | Change |
| --- | --- |
| `CHANGELOG.md` | a `1.1 — not yet released` section: the A0 §B surfaces (Linux container, `.deb` / `.rpm` / `.tar.gz` for `linux-x64` and `linux-arm64`, `win-x64` ZIP, `win-x64` MSI and service, bundled Web `4148c4bc…`, FFmpeg runtime `f28cc918…`) and the A0 §C exclusions. `[Unreleased]` is kept. The sentence "Only the Linux container is a supported deployment surface" is rewritten |
| `docs/versioning-policy.md` | the one existing policy for how a release is cut. New §7, publication rules for 1.1 |
| `docs/distribution/install-windows.md` | new. Portable ZIP and MSI install documentation |
| `README.md` | "The supported way to install Tesserafin is the prebuilt container image" restated the container-only claim; rewritten to point at the native forms |
| `docs/distribution/W5-A3-docs.md` | this record |

### The ZIP pin, cited and not reopened

Every document that names the unsigned ZIP cites
`ci/windows/w5/accepted-unsigned-zip.json` as it stands:
`zipSha256` `c1f6261cb770bd3dcbf255f4dd15b287a7289adad5a619d31725c158cd20e2d8`,
`assembledAtHead` `41d3411c9838feec6650dd10ec89a1f198aafdeb`, run `34766464798`.
Each also says that the digest identifies the ZIP built from that commit and
that a build from any other commit differs, per W5-A1 §1.

### The install documentation, sourced

Every procedure in `install-windows.md` is a form a W2, W3 or W4 slice ran:

| Statement | Source |
| --- | --- |
| ZIP layout, one top-level directory, no state | W0 §6, W2-A2 §1 |
| start with explicit `--datadir --configdir --cachedir --logdir --webdir --ffmpeg`; relocatable | W2-A3 §1 |
| readiness: `503` is starting; `/` non-`503` is ready; cold migrations measured past 180 s | W2-A3 §2, W0 §2.3 |
| non-zero exit on fatal startup; clean exit `0` | W3-A0 §1.3 |
| ZIP service script verbs, refusals, not a second installer | W2-A5 §2–§3; run on a real SCM by W3-A0 control P |
| `msiexec /i` / `/x` with `/qn /norestart /l*v` | `ci/windows/w4/probe-msi-*.ps1` |
| layout, account, arguments, recovery, ACLs, Event Log source | W4-A0, W4-A2, W4-A3, W4-A6 |
| service left stopped; `sc start` / `sc stop` reach Running / Stopped | W0 §10, W4-A6 §1 |
| upgrade replaces binaries, keeps state, service stays stopped | W4-A4 §1 |
| `INSTALLFOLDER` remembered when the upgrade omits it | W4-A5 §1 |
| uninstall removes service, binaries, source; keeps state | W4-A0 §4, W4-A6 §1 |

## 2. What was not claimed

* **No repair how-to.** Repair is unexercised (W4-accepted §2).
* **No signing how-to**, no certificate, no vendor. The vendor stays UNDECIDED.
* **No download URL**, on tesserafin.org or anywhere. That is W5-A4.
* **No tag name and no date** written as if either exists.
* **No bit-identical MSI**, and no claim that the ZIP is bit-identical across
  every future rebuild.
* **No downgrade behaviour.** W4-A4 recorded one downgrade exit code and graded
  nothing on it.
* **No upgrade from a running service**, no `INSTALLFOLDER` relocation on
  upgrade, no uninstall from Settings: none was measured, and the document says
  so where an operator would look.
* **No SMB / domain service identity.** W0 §9.2 names it as a deviation; no
  slice measured one.
* **No 1.0.0 section** in `CHANGELOG.md` (O-2 on A0).
* **W5 is not accepted** by this document.

## 3. Observed and left alone

Outside this ruling's authorised paths, and therefore recorded rather than
edited:

* `docs/admin-guide.md:15` — "**Supported surface: the Linux container.** No
  Windows path semantics are claimed, because no test in this repository runs
  on Windows."
* `docs/support.md:74` — "…and Linux-container-only support."

Both repeat the sentence this slice rewrites in `CHANGELOG.md`, and both become
stale with it.

* `CHANGELOG.md` `[Unreleased]` still says "Tesserafin has not published a
  release yet", while `gh release list` shows a GitHub Release
  `Tesserafin 1.0.0 — Foundation` created 2026-08-05. Reconciling that is not
  this slice, and O-2 forbids inventing a 1.0.0 section to do it.

## 4. Status

Draft pull request, branch `w5/a3-1.1-docs`. Independent review is the next
gate. #276 stays open.
