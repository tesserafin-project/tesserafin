# OneAmp DSP-1: source audit and adapter decision

Status: proposed decision, 2026-10-20. Tracks [DSP-1 / #308](https://github.com/tesserafin-project/tesserafin/issues/308), under [#307](https://github.com/tesserafin-project/tesserafin/issues/307). This change adds documentation and an isolated audit probe; it adds no runtime dependency or processing.

## Decision

Use OneAmp's standalone PCM `dsp::Processor` as an optional client capability. Keep Tesserafin's player, decoder selection, transport, authentication, queue, sessions and progress authoritative. Do not embed OneAmp's complete player or HTTP stack to obtain an equalizer.

Select **Web, local stereo music, opt-in and off by default** as the first demonstrator. The existing Web audio element and normalization graph provide a concrete integration point. Android Media3 remains a later adapter: the native client has no established playback pipeline yet, and the JNI package currently includes OneAmp's player and HTTP dependencies.

This follows [#146](https://github.com/tesserafin-project/tesserafin/issues/146): playback semantics belong to core; substantial DSP dependencies remain optional client implementation. No public plugin SDK or marketplace is a prerequisite. The capability must work locally without a Tesserafin account or hosted service. Themes own presentation, never the audio graph, credentials or playback state. OneDrop, casting, scene recognition and adaptive processing (#304) are excluded.

## Audited revisions and evidence

| Repository | Revision | Evidence |
| --- | --- | --- |
| [OneAmp](https://github.com/all3f0r1/oneamp/tree/cbf04e7ba97f000676c829257ce215e41281dd84) | `cbf04e7ba97f000676c829257ce215e41281dd84`, version 1.4.0 | Merged embedding work from [PR #3](https://github.com/all3f0r1/oneamp/pull/3); not an unmerged proposal |
| [Tesserafin server](https://github.com/tesserafin-project/tesserafin/tree/0f7cce7532a4f155e93f292cf9a04d3bf679772c) | `0f7cce7532a4f155e93f292cf9a04d3bf679772c` | Normalization scanner, encoding filters and playback contracts |
| [Tesserafin Web](https://github.com/tesserafin-project/tesserafin-web/tree/c370d07fa994cff3b7a8bae8766034f3690d6582) | `c370d07fa994cff3b7a8bae8766034f3690d6582` | Audio element, gain graph, HLS setup and playback manager |

Evidence levels must remain separate:

- **Source inspected:** 42 retrieved files were checked against their pinned Git blob hashes. API and lifecycle findings below come from that code, not only the embedding README.
- **Upstream executed:** [CI run 39213052164](https://github.com/all3f0r1/oneamp/actions/runs/39213052164) succeeded at the pinned OneAmp revision. The [embedding job](https://github.com/all3f0r1/oneamp/actions/runs/39213052164/job/117930538668) logs verify core feature checks, the PCM example, release WASM plus the Node worklet harness, two Android cross-compilations and the desktop JVM bridge examples. The WASM harness reports 95 frames of latency.
- **Locally executed:** the [audit probe](oneamp-worklet-probe.mjs) executes the actual pinned JS with a mock ABI. It isolates the worklet capacity defect below. It does not execute Rust, real WASM, a browser, ART or audio hardware.
- **Not established:** a Tesserafin integration, browser compatibility, device performance, allocation-free JNI/worklet control, gapless playback, A/V sync, true-peak compliance and Android packaging on physical devices. No local Rust or server build was run for this documentation change.

## Verified DSP and API surface

Sources: [core features](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-core/Cargo.toml), [processor](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-core/src/dsp/mod.rs), [stages](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-core/src/dsp/stages.rs), [layouts](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-core/src/dsp/channels.rs), [settings](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-core/src/settings.rs).

| Capability | Observed behavior | Host consequence |
| --- | --- | --- |
| Embedding | `oneamp-core` with `default-features = false` exposes PCM DSP without player/HTTP/device output | Select this dependency graph; defaults include desktop/device features |
| PCM | In-place interleaved `f32`; configured rate 8–384 kHz, 1–16 channels | Validate complete frames and finite samples; use the actual decoded PCM format |
| Layout | Rust supports explicit speakers/masks; count-only constructors assume a canonical layout | Counts do not prove speaker order; bindings cannot describe arbitrary masks |
| EQ/preamp | Ten fixed bands, gains and preamp clamped to ±12 dB; smoothed changes | Host selects curves; do not promise arbitrary parametric EQ |
| ReplayGain | Track/album/Auto gain from host metadata; optional peak-based cap | Host supplies queue context, units and provenance; no automatic media scan here |
| Other stages | Loudness compensation, mono, balance and volume; stereo fold-down; final lookahead limiter | Loudness compensation is not a LUFS detector or adaptive normalization |
| Order | Media gain → fold-down → EQ → loudness → mono → preamp/volume → balance → limiter | Preserve one normalization owner and one user-volume application |
| Wide PCM passthrough | Retains channels; gain and limiter remain, stereo stages inactive | This is **not** encoded Dolby/DTS/IEC 61937 passthrough |
| Delay | Limiter reports `round(rate × .002) - 1` frames (95 at 48 kHz; 87 at 44.1 kHz) | Query the value; flat parameters still delay PCM |
| Settings | `AudioSettings` schema version 1, defaults for missing fields, unknown fields ignored, unsupported-version error path | Parse/persist off the render thread; bind a Tesserafin schema separately |
| Processor state | `params`, `active`, `latency_frames`, `output_channels`, `is_bypassed` | Full settings/player features are not automatically exported through the bindings |

`Processor` supplies no standalone resampler, session clock, transport, decoder, crossfade scheduler or gapless queue. Full-player features do not become host-adapter features by linking the PCM processor. Input PCM sanitization is a host responsibility; parameter sanitization alone does not protect against non-finite audio samples.

The limiter caps sample peaks at 1.0. Its source does not establish an oversampled true-peak ceiling or loudness-standard certification. Do not label it a true-peak limiter or promise a target integrated LUFS.

## Existing Tesserafin processing

The [Web audio player](https://github.com/tesserafin-project/tesserafin-web/blob/c370d07fa994cff3b7a8bae8766034f3690d6582/src/plugins/htmlAudioPlayer/plugin.js) already creates `MediaElementAudioSourceNode → GainNode → destination` when normalization is used. TrackGain selects track metadata with album fallback; AlbumGain reverses that preference. It converts dB using `10^(gain/20)`. Safari also incorporates element volume in this gain path. Creating a second media-element source is not a valid integration strategy: introduce one graph owner and reuse the existing source/context/gain. Preserve the established volume behavior exactly once.

The [playback manager](https://github.com/tesserafin-project/tesserafin-web/blob/c370d07fa994cff3b7a8bae8766034f3690d6582/src/components/playback/playbackmanager.js) supplies album normalization metadata. The existing element time remains the basis of progress; processed sample counts must not replace session time.

The [server scan](https://github.com/tesserafin-project/tesserafin/blob/0f7cce7532a4f155e93f292cf9a04d3bf679772c/Tesserafin.Server.Core/ScheduledTasks/Tasks/AudioNormalizationTask.cs) runs FFmpeg `ebur128` into a null output and records measurements. A scan does **not** mean delivered audio has already been normalized.

The inspected [encoding filter path](https://github.com/tesserafin-project/tesserafin/blob/0f7cce7532a4f155e93f292cf9a04d3bf679772c/Tesserafin.Controller/MediaEncoding/EncodingHelper.cs) can downmix wider input to stereo and add `DownMixAudioBoost`. Its [configuration default](https://github.com/tesserafin-project/tesserafin/blob/0f7cce7532a4f155e93f292cf9a04d3bf679772c/Tesserafin.Model/Configuration/EncodingOptions.cs) is amplitude 2, approximately +6.02 dB. Thus delivered PCM may already have different channels and gain from the original media. This is not a global claim about every server encoding path.

| Delivery/output path | DSP eligibility and duplicate-processing rule |
| --- | --- |
| Direct Play, browser-decoded PCM | Eligible after format/origin checks; local DSP does not turn the server delivery classification into Transcode |
| Remux, client-decoded PCM | Potentially eligible; remux alone says nothing about gain provenance |
| Progressive transcode or decoded HLS | Potentially eligible using **delivered** PCM format; account for server filters/downmix; deferred from the first demo |
| Server-scanned gain metadata | Measurement only; apply gain in one chosen client stage |
| Gain already baked into delivered samples | Do not reapply the same normalization; unknown provenance means no additional automatic gain |
| Encoded passthrough, offload, receiver-owned playback | No accessible PCM contract: bypass with a reason; do not silently force decoding or transcoding for EQ |

First-demo normalization remains in the existing Web gain node; OneAmp ReplayGain stays Off. Later migration may transfer that responsibility atomically, never leave both active.

## Binding gaps and ownership

Sources: [WASM ABI](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-wasm/src/lib.rs), [worklet](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-wasm/www/oneamp-worklet.js), [Web helper](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-wasm/www/oneamp.js), [JNI](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-android/src/lib.rs), [Kotlin](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-android/kotlin/org/oneamp/OneAmpProcessor.kt), [Android dependency graph](https://github.com/all3f0r1/oneamp/blob/cbf04e7ba97f000676c829257ce215e41281dd84/oneamp-android/Cargo.toml).

| Finding | Evidence / consequence | Work owner |
| --- | --- | --- |
| G1: worklet fixes capacity at 128 and ignores returned sample count | Mock probe: 64/128-frame outputs finite; 256-frame stereo output has 256 non-finite samples after native clamp | Upstream helper fix or tested Tesserafin wrapper: bounded chunking, explicit counts and safe fallback |
| G2: worklet JSON control allocates | `params` constructs JSON, TextEncoder and WASM allocations on the worklet thread | Wrapper uses bounded typed scalar transactions applied between blocks; initialization/rebuild off live rendering |
| G3: no worklet EOS/drain protocol | Native flush exists, helper has reset/dispose but no flush message | Host wrapper must drain/count tail and acknowledge lifecycle epochs |
| G4: multichannel mode update discrepancy | Source inspection: `Chain::apply(..., false)` preserves old mode; `configure` never adopts requested mode, contrary to setter comment | Upstream regression/review needed; not executed here. Rebuild for mode changes; exclude multichannel initially |
| G5: JNI direct buffer uses address and capacity | It does not use Java position/limit; array variant copies and can grow scratch | Android adapter validates bounded aligned native-order slices, whole frames and counts; pool conversion buffers |
| G6: Kotlin read lock is not setter/process serialization | Both use shared read locks; documented same-thread contract remains necessary | Host has a single processing owner and queued commands; close outside callback. No end-to-end lock-free claim |
| G7: Android package includes player + HTTP | Even PCM use pulls those core features | Upstream/package split before adopting a DSP-only Android artifact |
| G8: bindings expose less state than Rust | No complete effective-state snapshot, explicit layout masks or format reconfigure; no ABI-version handshake | Host version/hash pin, constrained capabilities and observable acknowledgements; rebuild on format change |

These are adoption gates, not proof that the complete DSP engine is unusable. Allocation behavior still needs measurement across the complete chosen hot path, including first block, reset, message handling and teardown.

Reproduce G1 with Node and an existing OneAmp checkout containing the audited commit:

```sh
git -C /path/to/oneamp show cbf04e7ba97f000676c829257ce215e41281dd84:oneamp-wasm/www/oneamp-worklet.js > /tmp/oneamp-worklet-audited.js
node docs/audio/oneamp-worklet-probe.mjs /tmp/oneamp-worklet-audited.js
```

The probe rejects any other Git blob. Success means the documented limitation was reproduced, not that the helper passed a production-readiness test.

## Platform feasibility, packaging and licensing

**Web:** self-host a pinned, reproducibly built WASM and wrapper with content hashes and retained notices. Compile/fetch before enabling; serve WASM correctly and validate CSP/worklet loading. No CDN, account, new media fetcher or credential storage is needed. Scalar WASM does not itself require SharedArrayBuffer/cross-origin isolation.

[Web Audio 1.1](https://www.w3.org/TR/webaudio-1.1/) describes configurable render quantum sizes and silence for media-element sources whose media is CORS-cross-origin. AudioWorklet requires a secure context. The [current helper](https://github.com/tesserafin-project/tesserafin-web/blob/c370d07fa994cff3b7a8bae8766034f3690d6582/src/components/htmlMediaHelper.js) uses no crossOrigin value for remote media and anonymous for other media, with further credential handling in the player. Ability to play an element does not prove ability to process it. Start with verified same-origin media on HTTPS/localhost; keep ordinary playback available on unsupported origins, insecure LAN HTTP, CORS failure or worklet failure. Do not alter playback-credential routing to make DSP work.

**Android:** an eventual Media3 audio-processor adapter is plausible, not implemented. The [official sink builder reference](https://developer.android.com/reference/androidx/media3/exoplayer/audio/DefaultAudioSink.Builder) says processing chains are for PCM, not passthrough/offload, and float output disables audio processing in that documented path (consulted 2026-10-20; not a pinned Media3 selection). Do not assume `setEnableFloatOutput(true)` plus a custom processor works. Pin a Media3 version and prove the actual sink route; likely need deliberate PCM16↔f32 conversion or a separately validated float-capable route. Preserve position/limit semantics, EOS, flush/reset and output counts. Cross-compilation and desktop JVM execution are not ART, AudioTrack, Media3 or device tests.

Upstream CI builds aarch64 and x86_64 JNI with NDK API 24; this does not establish AAR delivery, all ABI coverage, native-library page-size compatibility or physical-device readiness. Define those checks when the Android playback slice exists.

**Licensing:** workspace manifests declare `MIT OR Apache-2.0`. This is source-level license identification, not a completed distribution/legal audit. Before shipping either adapter: select and record a license, preserve notices, inventory the exact locked dependency graph and generated binaries, verify redistribution requirements and produce checksums/SBOM. Selecting PCM-only core reduces scope; do not import decoder/codec licensing assumptions from the full-player documentation.

## Follow-up

The proposed [host contract](oneamp-host-contract.md) and [DSP-2 Web demonstrator](oneamp-dsp-2-web-demonstrator.md) define the next bounded slice, tracked separately in [tesserafin-web #211](https://github.com/tesserafin-project/tesserafin-web/issues/211). No runtime support is claimed until that slice passes its gates. Re-pin and recheck these findings when the upstream revision or client playback path changes.
