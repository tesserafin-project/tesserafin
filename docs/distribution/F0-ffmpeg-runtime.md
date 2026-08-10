# F0 — the Tesserafin FFmpeg runtime

Issue [#229]. This document is the distribution contract for the FFmpeg runtime
that Tesserafin's native Linux packages will carry: what it is built from, what
is inside it, what it is licensed under, what it can be claimed to do, and — as
importantly — what is deliberately absent.

It exists because Tesserafin cannot redistribute someone else's binary. PR #228
bundled the upstream `jellyfin-ffmpeg` portable build; independent acceptance of
that PR found it reports GPLv3-or-later while the package claimed
GPL-2.0-or-later, that it is configured with `--enable-libfdk-aac`, that no
licence text or corresponding source ships with it, and that its VAAPI path
aborts on two of the three declared lifecycle environments. None of that is
fixable by wrapping the binary in a notice.

Nothing here publishes anything. There is no release, no channel, no signing key
and no repository.

---

## 1. Source baseline

| Input | Value |
| --- | --- |
| Project | `jellyfin/jellyfin-ffmpeg` |
| Release baseline | `v7.1.4-3` |
| Resolved commit | `d4590e12452f94d40e413caecb34b08de608353b` |
| Commit signature | GPG **verified** |
| Tesserafin build revision | `7.1.4-tesserafin.1` |

Stable only. Jellyfin-FFmpeg 8.1 prereleases, FFmpeg 9.0, nightlies and moving
branches are out of scope; FFmpeg 9.0 is a separate migration.

**`v7.1.4-3` is a lightweight tag.** The tag-object endpoint returns 404 and the
ref points straight at a commit, so there is no signed *tag*. The immutable
anchor is the commit SHA, and that commit is itself GPG-verified. Recorded as a
deviation from "resolve the signed tag", not treated as a blocker: a commit SHA
is content-addressed and cannot be moved under us.

### Why the fork rather than upstream FFmpeg 7.1.4

`Tesserafin.MediaEncoding/Encoder/EncoderValidator.cs` lists `tonemapx` among its
required filters, and `tonemapx` exists only in the Jellyfin tree. The fork is
therefore the baseline. The fork-versus-upstream patch delta is shipped as an
inventory in the corresponding-source artifact rather than cherry-picked, so the
baseline stays the named one instead of becoming something this project invented.

### Why upstream's builder is replaced rather than reused

`builder/` cannot be pinned:

* `builder/images/base/Dockerfile` is `FROM ubuntu:noble` — a moving tag —
  followed by `apt-get dist-upgrade`;
* `builder/images/base-linux64/Dockerfile` clones **crosstool-NG from `master`
  with no ref** and builds an entire toolchain;
* it clones **`Implib.so` from `master` at `--depth=1`**;
* only the ~40 `builder/scripts.d/*` component scripts carry a `SCRIPT_COMMIT`.

A digest-pinned builder environment and bit-for-bit reproducibility are both
required here, and neither is reachable from that base.

`Implib.so` matters for a second reason. `builder/scripts.d/50-vaapi/50-libva.sh`
builds libva from a recent commit and then runs `gen-implib` over it, replacing
the library with a trampoline stub that `dlopen`s the *host's* `libva.so.2` and
resolves symbols with `dlsym`. When the host libva is older than the one the
stub was generated from, the missing symbol becomes `assert(0)`:

```
implib-gen: libva.so.2: failed to resolve symbol 'vaMapBuffer2' via dlsym
ffmpeg: libva.so.2.init.c:290: _libva_so_2_tramp_resolve: Assertion `0' failed.
```

Tesserafin links libva normally, from a pinned old release. That is the fix.

---

## 2. Licence shape

The runtime is **GPL-3.0-or-later**, and it says so.

`--enable-gpl` is required by x264 and x265, both GPL-2.0-or-later.
`--enable-version3` is required because the closure contains OpenSSL 3, which is
Apache-2.0 — GPLv3-compatible, GPLv2-incompatible. OpenSSL provides the `https`
protocol, which Tesserafin needs for remote media sources, so the choice is
version3 or no TLS. It is version3, stated rather than inherited.

Tesserafin's own server code is GPL-2.0. That is not a conflict: the server
*invokes* FFmpeg as a subprocess, so the two are aggregated, not combined. What
was wrong in #228 was not the GPLv3 — it was a package claiming
`GPL-2.0-or-later` while shipping GPLv3 material with no notices and no source.

Permitted licences for a component, enforced by
`ci/ffmpeg/verify-components.sh`: Apache-2.0, BSD-2-Clause, BSD-3-Clause,
BSD-3-Clause AND AOM-Patent-License-1.0, FTL OR GPL-2.0-or-later,
GPL-2.0-or-later, ISC, LGPL-2.0-or-later, LGPL-2.1-or-later, MIT, WTFPL, Zlib.
Anything else stops the build until it is classified. There is no prose
allowlist.

### Hard exclusions

* `libfdk_aac` — FFmpeg classifies it nonfree. FFmpeg's native AAC encoder
  covers the requirement. `--disable-libfdk-aac` is stated explicitly so that
  removing it shows up in a diff.
* `--enable-nonfree` — stated as `--disable-nonfree` for the same reason.
* Any component whose licence is not in the permitted set.

Excluding libfdk-aac does not change server behaviour.
`EncoderValidator.GetCodecs()` computes `found = ffmpeg ∩ _requiredEncoders`: the
list is an intersection filter, not a mandatory set, so a component that is
absent is simply never advertised. `_requiredEncoders` does name `libfdk_aac`,
which is exactly why this needed checking rather than assuming.

---

## 3. Capability requirement

Derived from `EncoderValidator._requiredEncoders`, `_requiredDecoders` and
`_requiredFilters` — what the server actually looks for — not from what
Jellyfin's builder happens to include.

| Requirement | Provided by |
| --- | --- |
| H.264 software encode | x264 |
| HEVC software encode | x265 |
| AV1 software encode | SVT-AV1 |
| AV1 decode | dav1d |
| VP8/VP9 decode | libvpx |
| H.264/HEVC/MPEG-2/MPEG-4 decode | native |
| AAC encode and decode | **native** — deliberately not libfdk-aac |
| AC-3, AC-4, ALAC, DCA, FLAC, TrueHD | native |
| MP3 encode | LAME |
| Opus, Vorbis | opus, libvorbis (+ libogg) |
| `zscale` | zimg |
| `tonemapx`, `alphasrc` | native (fork patch) |
| Subtitle burn-in | libass + freetype + fribidi + harfbuzz + fontconfig + libunibreak |
| SubRip (`srt`) encode | native — *not* libsrt, which is a transport |
| HLS segmenting, image extraction, `ffprobe` JSON | native |
| VAAPI encode/decode/filters | libva 2.17 + libdrm |
| QSV | libvpl |
| NVENC/NVDEC/CUVID and `*_cuda` filters | nv-codec-headers |
| AMF | AMF headers |
| `*_opencl` filters | OpenCL headers |

### Known capability gap

**`libplacebo` and the Vulkan filters are not in build revision
`7.1.4-tesserafin.1`.** `_requiredFilters` names `libplacebo`, so this is a real
regression against the container image: Vulkan tone-mapping is unavailable and
the server falls back to `tonemap_vaapi`, `tonemap_opencl` or the software
`tonemapx` path. It is deferred because the shader toolchain — libplacebo,
Vulkan-Headers, shaderc, glslang, SPIRV-Tools, SPIRV-Headers — is the largest
single source-closure and reproducibility risk in the component set. It is named
here rather than dropped quietly, and it is the first candidate for
`7.1.4-tesserafin.2`.

Rockchip (`rkmpp`, `rkrga`) is outside the declared `linux-x64`/`linux-arm64`
matrix. macOS paths (`aac_at`, `*_videotoolbox`, `scale_vt`) do not apply.

---

## 4. Architectures and the portability floor

`linux-x64` and `linux-arm64`. Both built on architecture-native hosted runners;
runtime acceptance is always architecture-native, never emulated.

**GLIBC floor: 2.34.** Measured on the declared environments — Rocky 9 = 2.34,
Debian 12 = 2.36, Ubuntu 24.04 = 2.39, Fedora 42 = 2.41 — so Rocky 9 sets the
ceiling. The builder is `debian:11` (glibc 2.31), pinned by digest, and the
highest `GLIBC_2.x` symbol referenced by the produced binaries is gated at
≤ 2.34.

**libva floor: 2.17**, the oldest in the declared matrix (Debian 12). libva is
built from that pinned release and linked normally. On every declared
environment, `ffmpeg -hwaccels` and the server-style hardware probe must return
without aborting, and an unavailable device must produce a controlled
"device creation failed", not `SIGABRT`. A runtime that survives only because
the caller wraps the subprocess is not accepted.

---

## 5. Hardware claims

Compiled capability and runtime capability are recorded separately, always.

Listing an encoder proves it is compiled in. It does not prove it operates. A
hardware path is claimed as working only where matching physical hardware
produced the evidence; everywhere else the capability manifest says
**"not runtime-tested"**. Hosted CI has no GPU and makes no hardware claim.

---

## 6. Redistribution closure

Every runtime archive ships `LICENSES/`, `THIRD_PARTY_NOTICES.md`, `SOURCE.json`,
the exact build configuration, and the name and SHA-256 of the corresponding
source artifact.

A single deterministic corresponding-source artifact carries the FFmpeg source,
every applied patch, complete source for every statically linked dependency, the
exact build scripts and configuration, the dependency manifest, and instructions
sufficient to rebuild both runtime archives. It holds the preferred form for
modification — real source trees, not links or binaries.

This is the engineering closure that has to exist before legal review. It is not
a legal ruling, and this document does not make one.

---

## 7. Reproducibility

Two independent clean hosted builds per architecture must produce identical
SHA-256 values for `ffmpeg`, `ffprobe`, the runtime archive, the SBOM, the
provenance manifest, the capability manifest and the corresponding-source
archive. Functional equivalence is not accepted.

What makes it hold: a digest-pinned builder image so the compiler is an input
rather than something this build produces; `SOURCE_DATE_EPOCH`;
`-ffile-prefix-map`; `-Wl,--build-id=none`; deterministic archives; fixed
`LC_ALL` and `TZ`; sorted, ownerless tar members with clamped mtimes.

**No LTO.** Upstream's `defaults-gpl.sh` sets `--enable-lto=auto`; it is dropped
here. LTO partitioning depends on job count and link order and is the largest
single threat to bit-for-bit reproducibility. It buys throughput, not capability.

---

## 8. Update policy

The baseline moves only on a deliberate revision of this document. A component
version or digest change is a reviewed change to `ci/ffmpeg/components.json`,
never maintenance. A new component must arrive with a licence classification, an
immutable pin and a named Tesserafin capability that requires it, or the gate
refuses it.

---

## 9. What this work does not do

No publication, no release, no channel, no registry, no APT/DNF repository, no
signing key, no new credential. Docker's FFmpeg runtime is untouched. Server
playback and transcoding behaviour is untouched. Web, Android and OpenAPI are
untouched. PR #228 is untouched.
