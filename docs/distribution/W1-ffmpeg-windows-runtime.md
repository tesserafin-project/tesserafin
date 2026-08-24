# W1-A3 — the win-x64 FFmpeg runtime

Issue [#236]. Follows W1-R, which retained and published the Windows build
inputs; see [W1-windows-build-input-retention.md](W1-windows-build-input-retention.md).
The Linux runtimes are [F0-ffmpeg-runtime.md](F0-ffmpeg-runtime.md) and are not
changed by this work.

This document describes a **candidate**. Nothing here is published, signed,
tagged or released, and no downstream work may consume it until an owner ruling
decides how it is retained durably. An expiring Actions artifact is not a usable
input for anything.

---

## 1. The frozen premise

Every identity below is an input, not a result. A build that cannot reach all of
them stops rather than substituting something close.

| Thing | Value |
| --- | --- |
| Server base | `688436081e0ae6d9954af38b5fe306b6b853be7b` (master, the base of #253) |
| Proof head | the head of #253; the exact SHA every run built is recorded in the pull-request body, because a document cannot name a commit it is itself part of |
| jellyfin-ffmpeg | `d4590e12452f94d40e413caecb34b08de608353b` (`v7.1.4-3`) |
| Build revision | `7.1.4-tesserafin.1` |
| Build inputs | `ghcr.io/tesserafin-project/windows-ffmpeg-build-inputs@sha256:cff23b74…c04f0a` |
| Layer | `sha256:03b43119…de271e` |
| Lock | `602510cd…5abcb8`, 246 packages, 246 valid signatures |
| Trust root | `1c32ec73…16a49c` |
| Signer | `5F944B027F7FE2091985AA2EFA11531AA0AA7F57` |

#### A note on the earlier base

An earlier revision of this document named `cc6ee781c732d9999b9619df68fc26c6a34dbfdc`
as the server commit. That is the base of **#250 (W1-A2)**, the historical lane,
and it is not this candidate's base. #253 (W1-A3) replays the same sixteen
commits onto the post-#252 master `688436081e…` — range-diff equal, identical
aggregate patch-id, every touched blob byte-identical — and then carries the
measured repairs above it. #250 keeps its own history and its own runs; nothing
here is evidence for it, and nothing there is evidence for this.

They are committed in
[`ci/windows/ffmpeg/accepted-build-inputs.json`](../../ci/windows/ffmpeg/accepted-build-inputs.json)
rather than passed to the workflow. The proof takes a commit SHA as its only
input, so dispatching it cannot choose a different package: the package identity
travels with the tree being proved.

---

## 2. What the build may and may not do

**Native only.** `windows-latest`, MSYS2 `CLANG64`, `win-x64`. The build script
refuses to run outside a CLANG64 shell and refuses a non-x86-64 host. There is no
Wine path and no cross-build path, and if GitHub cannot allocate two native
runners the correct outcome is *acceptance pending* — not a weaker proof.

**The toolchain is an input.** It arrives as the 246-package MSYS2 set W1-R
retained, pulled anonymously by digest, every signature verified against the
trust root that travelled inside the layer, installed with `pacman -U` over local
files with every mirror emptied first. That last part is the proof rather than
the tidiness: if the locked set were incomplete, pacman would need a repository
and would fail, so a successful install with no mirror configured is evidence
that nothing upstream was consulted.

**Nothing precompiled.** No Chocolatey, no vcpkg, no winget, no system FFmpeg,
no Jellyfin release asset. `ci/windows/ffmpeg/negative-controls.py` scans the
files this work owns for every one of those acquisition shapes and fails on a
match, with comments and docstrings stripped first so the control cannot be
weakened by its own explanation.

---

## 3. The patch series: 95/95, and what that costs

The fork ships its 95 changes as a quilt series rather than pre-applied.
Configuring the checkout directly would produce plain FFmpeg 7.1.4 — no
`tonemapx`, no `alphasrc` — while every manifest still named the fork.

The Linux runtime applies **94 of 95**. It excludes
`0029-remove-fdk-aac-from-nonfree.patch`, which deletes `libfdk_aac` from
FFmpeg's own `EXTERNAL_LIBRARY_NONFREE_LIST`, so that FFmpeg's upstream licence
enforcement remains a fourth independent layer beneath Tesserafin's own.

**win-x64 applies all 95, as a declared platform exception.** Applying 0029 does
not enable FDK AAC — it only stops FFmpeg from classifying it as nonfree — but it
does remove that fourth layer, and that is a real difference from the Linux
posture.

It is not stated only in prose. `ci/ffmpeg/fork-patches.json` records the
divergence as machine-readable data on the patch itself:

```json
"platformExceptions": [
  { "platform": "win-x64", "applied": true,
    "rationale": "…", "compensatingControls": [ "…", "…", "…" ] }
]
```

That object is closed to unknown fields, its `platform` must be one of
`components.json`'s declared platforms, and an exception whose `applied` matches
the base decision is refused — an exception that changes nothing records nothing.
Everything downstream reads it rather than restating it:

* `ci/ffmpeg/verify-components.sh --platform <rid>` reports the **effective**
  decision for one platform. It no longer reports one platform's exclusion count
  while checking another platform's flags: `--platform win-x64` says
  `95 of 95 patches applied, 0 excluded`, `--platform linux-x64` says
  `94 of 95 patches applied, 1 excluded`, and the exception inventory is printed
  in both.
* `ci/windows/ffmpeg/build-win-x64.sh` computes its applied set from the same
  file. Applying fewer patches than the decision and applying more are separate
  failures with separate messages.
* `provenance.json` carries `patches.platformExceptions` beside `patches.excluded`,
  so a reader of the delivered artifact alone can see the divergence.
  `excluded: []` on its own cannot distinguish "nothing diverged" from
  "everything diverged and nobody said so".
* `ci/windows/ffmpeg/verify-linux-identity.sh` compares the **decision** rather
  than the file bytes, so recording a Windows exception cannot quietly change
  what Linux applies.

Four independent layers still keep FDK AAC out of this runtime.

1. `libfdk-aac` is not a component in `ci/ffmpeg/components.json` at all, so
   its source is never fetched and never built;
2. the flag file states `--disable-libfdk-aac` and `--disable-nonfree`
   explicitly, and `ci/ffmpeg/verify-components.sh` refuses the file if either is
   missing or if the enabling form appears;
3. `verify-runtime.py` reads the configure line **out of the produced binary**
   and refuses `--enable-libfdk-aac` or `--enable-nonfree` there;
4. `capability.json` is generated by running the produced binary, and both the
   static gate and `accept-runtime.ps1` refuse any encoder or decoder whose name
   contains `fdk`.

The series having run is not the same as the series having landed, so the build
also asserts `tonemapx` and `alphasrc` in `libavfilter/allfilters.c` after
applying, and `accept-runtime.ps1` reads `tonemapx` back out of the relocated
binary at the end.

Zero fuzz is enforced with `patch -F0 --forward`. The series is pinned by the
same commit as the tree, so anything but a clean apply means a pin moved.

---

## 4. Configuration

[`ci/windows/ffmpeg/ffmpeg-configure.win-x64.txt`](../../ci/windows/ffmpeg/ffmpeg-configure.win-x64.txt)
is a **complete, self-contained** flag list. It deliberately does not extend
`ci/ffmpeg/ffmpeg-configure.txt` the way the two Linux architecture files do.

That file states `--enable-openssl`, `--enable-vaapi`, `--enable-libdrm` and a
Linux prefix, and the additive mechanism has no subtractive form — there is no
flag that takes back an `--enable-` stated in a common file. Four of the
contract's hardest guarantees would then hold only through the interaction of two
files. The cost is that the two flag sets can now drift, so
`ci/windows/ffmpeg/verify-linux-identity.sh` proves the Linux files and their
resolved flag lists are byte-identical to `origin/master`, and the same
`ci/ffmpeg/verify-components.sh` that gates the Linux flags is run against the
Windows file with `--flags`.

**Licence shape.** `--enable-gpl --enable-version3 --disable-nonfree
--disable-libfdk-aac`. The runtime is distributed under GPL-3.0-or-later.

**TLS is Schannel and nothing else.** `--enable-schannel --disable-openssl
--disable-gnutls --disable-libtls --disable-securetransport`. The two
alternatives are disabled explicitly *because* the locked MSYS2 set contains both
openssl and gnutls as dependencies of curl; leaving either to autodetection would
let a transitive package choose this runtime's TLS provider. Schannel is proven
twice: in the configure line the binary reports, and as `secur32.dll` in
`ffmpeg.exe`'s import table.

**No LTO.** `--disable-lto` is stated outright. LTO partitioning depends on job
count and link order and is the largest single threat to reproducibility.

**Hardware surfaces compiled**: DXVA2, D3D11VA, D3D12VA, AMF, QSV via libvpl,
and the NVIDIA stack (ffnvcodec, NVENC, NVDEC, CUVID, CUDA, CUDA-LLVM). *A
compiled capability is what the binary can attempt. It is never a claim that this
or any other machine has the device or the driver for it.* Nothing in this work
tests hardware encoding.

**Named gaps**, recorded rather than silently dropped:

| Gap | Why |
| --- | --- |
| OpenCL and the `*_opencl` filters | Not in the win-x64 hardware surface this work was scoped to. `opencl-headers` and `opencl-icd-loader` are restricted to the Linux platforms in `components.json`. |
| libplacebo / Vulkan tone-mapping | Deferred for the whole `7.1.4-tesserafin.1` revision, on both operating systems. See F0. |
| MediaFoundation encoders | Disabled explicitly. FFmpeg autodetects it on Windows, so silence would have meant "whatever the environment offered". |
| libxml2, iconv, lzma, bzlib | All present in the locked package set and all autodetected. Disabled explicitly for the same reason. |

---

## 5. Components

`ci/ffmpeg/components.json` gained a declared `platforms` dimension. Adding
`win-x64` to a manifest whose implicit rule was "a component with no
`architectures` key applies everywhere" would have made libva, libdrm, openssl,
opencl-headers and opencl-icd-loader Windows-applicable — the exact five this
contract excludes. Every non-portable component now says which platforms it
belongs to and why, and the validator refuses an architecture value that is not a
declared platform.

* **21 components** are win-x64-applicable. `build-win-x64.sh` refuses to run if
  the manifest makes a component applicable that it has no recipe for, so
  widening the manifest cannot quietly narrow the runtime.
* **26 / 24** components for linux-x64 / linux-arm64 — unchanged, and proven
  unchanged by name, pin, licence and order.
* `amf-headers` and `libvpl` are now `linux-x64` + `win-x64`.

### Component patches

Two frozen things can disagree. The component source pins are frozen by
`components.json`; the toolchain is frozen by the 246-package MSYS2 lock. Where a
pinned source cannot be configured by the pinned toolchain, the gap is closed by
a patch in `ci/windows/ffmpeg/patches/`, listed in `series.txt` with its reason,
applied with `patch -p1 --forward -F0`, and recorded in the delivered provenance
with its SHA-256 — never by loosening a pin.

The rule is **build system only**. No patch there may touch codec, filter or
protocol source; a change to what the runtime computes belongs in a component pin
or in the FFmpeg fork series, where the existing gates can see it.
`verify-runtime.py` requires the provenance to describe exactly the series in the
tree, digests included, so "patched" cannot come to mean "some patch, once".

| Component | Why |
| --- | --- |
| x265 3.6 | CMake 4.4.2 refuses `cmake_policy(SET CMP0025 OLD)` and `CMP0054 OLD` outright — those policies were removed, not deprecated — and refuses `cmake_minimum_required (VERSION 2.8.8)`, since compatibility below 3.5 is gone. The patch sets both to NEW, raises the floor to 3.5 and moves the call before `project()`. Nothing under `source/common`, `source/encoder` or `source/x265.cpp` is touched. |

The Linux runtimes take none of these: their builder image carries cmake 3.18,
where every pinned component configures unmodified.

---

## 6. What is delivered

| Path | What it is |
| --- | --- |
| `runtime/tesserafin-ffmpeg-<rev>-win-x64.zip` | the unsigned, deterministic runtime archive: `bin/ffmpeg.exe`, `bin/ffprobe.exe`, `LICENSES/`, notices, capability and build configuration |
| `capability.json` | every encoder, decoder, filter, protocol and hwaccel, **read from the produced binaries** |
| `build-configuration.txt` | the configure line the binary itself reports |
| `pe-closure.json` | every delivered PE, its architecture, its ordinary and delay-loaded imports |
| `provenance.json` | the repository SHA, all W1-R identities, the sources, the patch inventory, the toolchain, the configuration, the capability digest and both archive digests |
| `sbom.cdx.json` | CycloneDX 1.5 |
| `THIRD-PARTY-NOTICES.md`, `licenses/` | one entry per component, and the licence text it names |
| `source/…-source.tar.zst` | the complete corresponding source, as fetched |
| `SHA256SUMS` | every delivered path |

The zip is built with fixed dates, fixed modes, sorted entries and a stated
compression level. The corresponding-source archive records **two** digests: the
container and the decompressed stream, because a zstd container can differ
between two identical trees.

The static linking is deliberate: `-static` resolves libc++, libunwind and
libwinpthread into the images, so the delivered closure is the operating system's
own DLLs and nothing else.
[`allowed-system-dlls.txt`](../../ci/windows/ffmpeg/allowed-system-dlls.txt) is
the whole permitted set; anything else must be delivered beside the binary or the
runtime is refused.

---

## 7. How it is validated

**Locally, before a hosted minute is spent** — every one of these runs on a
Linux workstation:

```bash
ci/ffmpeg/verify-components.sh                       # the catalogue view
ci/ffmpeg/verify-components.sh --platform linux-x64  # what Linux decides
ci/ffmpeg/verify-components.sh --platform win-x64 \
    --flags ci/windows/ffmpeg/ffmpeg-configure.win-x64.txt
ci/windows/ffmpeg/verify-linux-identity.sh
python3 ci/windows/ffmpeg/negative-controls.py
```

`--platform` is not cosmetic. Without it the report printed the base exclusion
count while checking the Windows flag file, so a declared platform exception read
as an exclusion that never happened.

The PE reader is standard library only and reads delay-loaded imports as well as
ordinary ones — a check that reads only the import directory concludes that a
binary with hardware support has none. Because it is pure Python, the
wrong-architecture and corrupted-path controls synthesise their own PE fixtures
and need neither a Windows runner nor a second toolchain.

**On the runner**: `verify-runtime.py` (static: architecture, closure, Schannel,
leakage, cross-links) then `accept-runtime.ps1` (behavioural: the archive is
extracted to an unrelated directory, PATH is reduced to the system directories,
the absence of any other ffmpeg is asserted, and a software encode → probe →
decode round trip is read back rather than trusted).

**Across two runner allocations**: `.github/workflows/w1-windows-ffmpeg-runtime.yml`
builds on two clean native `windows-latest` allocations, each pulling the same
digest independently with no cache anywhere, and `compare-hosts.py` requires the
same path set, then the same content, then the same archive bytes.

### What that proves, and what it does not

This is **dual-runner reproducibility**, not two-host independence, and the
distinction is enforced rather than described. `compare-hosts.py` reads both
`runner.json` records and records three facts in `comparison.json`:

| Field | Meaning |
| --- | --- |
| `distinctRunnerAllocations` | two different `runnerName` values. Required — one job compared against itself is refused outright. |
| `sameRunnerImage` | both allocations ran the same runner-image generation. |
| `sameNode` | both allocations landed on the same physical `node`. |

`topology` is then derived, never asserted: `dual-runner` when `sameNode` is
true, `two-host-same-image` or `two-host-distinct-images` when it is false. With
`sameNode` true, `independenceClaim` reads `none: …`, and the verdict line says
"two native Windows runner allocations", never "two hosts".

The measured result on this candidate is **`sameNode: true`**: GitHub placed both
allocations on one node. Nothing in this repository chooses that, and re-running
until a second node happens to be allocated would be selecting evidence rather
than producing it. So the proof states what the workflow guarantees — two
isolated jobs, two allocations, one runner-image generation, byte-identical
output — and states the limitation in the same record.

Both `runner.json` files, `comparison.json` and the acceptance evidence are
retained together for exactly this reason: split them and the limitation stops
travelling with the claim.

That workflow carries two triggers. `workflow_dispatch` takes an exact evidence
SHA and is the intended route once this reaches master; GitHub only offers it for
a workflow present on the default branch, and dispatching this one from a feature
branch answered `404`. The second trigger is `pull_request`, filtered to
`ci/windows/**`, `ci/ffmpeg/**` and the workflow file itself. It works before
merge, from the pull request head's own copy of the workflow, and it keeps
working after merge for every future change to those paths — unlike a
single-branch `push` trigger, which works once and then becomes dead
configuration on master. `EVIDENCE_SHA` resolves to
`github.event.pull_request.head.sha` for that event, never the ephemeral merge
commit, and every job asserts that its checkout is exactly that commit.

**Permanent negative controls** (58) refuse: missing, added, renamed and
corrupted paths — inside one delivered set and between two allocations; arm64,
x86 and PE32 images; a PE carrying a link timestamp; an embedded build-host path
in UTF-8 and in UTF-16LE; a tagged or non-digest input reference; a wrong
manifest, layer, lock, trust root or signer; a short package or signature count;
every shape of live pacman resolution; one allocation compared against itself; a
same-node comparison recorded as anything other than `dual-runner`; and five
separate ways the fork-patch decision can go wrong — a silently excluded patch,
0029 applied with no declared exception, an exception that changes nothing, an
unsafe patch applied by the base decision, and FDK AAC reappearing in the
configure line, the encoders or the decoders.

---

## 8. What this work does not do

* it publishes nothing — no GHCR push, no release, no tag, no signature, and no
  `packages: write` permission anywhere in the workflow;
* it makes no hardware-runtime claim;
* it does not begin W2, and W2 may not consume an expiring Actions artifact;
* it does not change the Linux runtimes, the OpenAPI surface, the SDK
  provenance, the web pair lock or any #153 surface.

---

## 9. What durable retention would have to cover

**Nothing here is authorised, and this section publishes nothing.** It states
what an owner ruling would have to require, so that the ruling is decided against
a written list rather than assembled from memory later.

Everything that constitutes this proof currently exists only as an expiring
Actions artifact. The runtime, its corresponding source, its inventory and the
dual-runner records all disappear on their retention date, which is why W2 cannot
consume any of it today.

### The co-retention set

These are one unit. Retaining a subset produces a binary whose licence
obligations cannot be met, or a claim whose limitation no longer travels with it.

| Item | Why it cannot be dropped |
| --- | --- |
| the win-x64 runtime archive | the artifact itself |
| the corresponding-source archive | GPL obligation; see below |
| `windows-ffmpeg-build-inputs@sha256:cff23b74…` | the 246-package MSYS2 set is the build's root of trust. Delete it and the accepted runtime stops being reproducible even if everything else here survives. |
| `provenance.json` | binds inputs, patch decision, platform exceptions and toolchain to the bytes |
| `sbom.cdx.json`, `licenses/**`, `THIRD-PARTY-NOTICES.md`, `SHA256SUMS` | the redistribution closure |
| `accept-runtime.json` | the behavioural acceptance. It is deliberately moved OUT of the delivered set before upload, so it lives only in the evidence bundles — the delivered set alone does not carry the encode/probe/decode proof. |
| both evidence bundles (`winx64-evidence-a`, `winx64-evidence-b`) | one bundle is one allocation; the comparison means nothing without both |
| `comparison.json` | the reproducibility record |
| both `runner.json` records | the `sameNode` limitation. Retain the comparison without these and the claim outlives the caveat that qualifies it. |

### The licence obligation is durable, not one-shot

The runtime is GPL-3.0-or-later (`--enable-gpl --enable-version3`, with
x264 and x265 GPL-2.0-or-later) and links every component **statically**. So the
corresponding source is not a convenience: it must remain available to anyone who
receives the binary, for at least as long as that binary is distributed. A store
the project can delete at will, or one with no stated retention, does not
discharge that. Any ruling that authorises distribution has to say how long the
corresponding source lives and how a recipient reaches it.

### What a ruling must still not grant

Publication of a candidate is not acceptance of one. A retention ruling may not
carry signing authority, release authority, a server ZIP, an MSI, a mutable tag
as an identity, or permission to begin W2. Consumption is by exact manifest
digest only, and the accepted digest belongs on [#236] before anything downstream
reads it.

[#236]: https://github.com/tesserafin-project/tesserafin/issues/236
