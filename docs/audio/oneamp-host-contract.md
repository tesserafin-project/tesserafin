# Proposed Tesserafin PCM host contract

Status: DSP-1 proposal; **not an existing API or shipped capability**. See the [source audit](oneamp-dsp-1-audit.md) for pinned evidence and binding gaps.

## Ownership and PCM boundary

Tesserafin owns transport/authentication, decode route, session identity, queue, media time, user volume, normalization provenance and fallback. The optional adapter owns only processing of host-supplied PCM. It receives no server token, URL, account or network provider.

Negotiate an immutable format before first PCM: actual sample rate, explicit speaker order, input/output channels, interleaved f32, maximum frames per call and processing latency. Rates must satisfy both core and adapter restrictions; the first Web slice permits actual context rates 44.1/48 kHz and known two-channel sources only. Do not infer layout from a forced stereo output or from source metadata after server conversion.

For Rust, each borrowed mutable slice is exclusively owned for the call; the processor retains its internal history, not the caller's audio buffer. For WASM, the host owns the handle and reuses its linear-memory view while valid. For JNI, only direct native-order aligned buffers with the exact payload start and bounded capacity are eligible. Rust layout support is wider than binding support.

Reject non-finite input, negative/oversized frame counts, non-integral frames, unsupported layouts/rates or insufficient output capacity at the boundary. Rust truncates incomplete frames and WASM/JNI may clamp counts: the adapter must not silently accept that as success. `process` and `flush` return **samples**; divide by negotiated output channels only after checking divisibility and bounds. Folded stereo output needs fewer samples than wide input.

No retain/reuse of an input buffer while processing is in flight. No transport reads, decode, filesystem I/O, user callbacks or clock ownership in the processor.

## Configuration and effective state

Persist a host-owned versioned intent off the audio thread. Do not store OneAmp's entire player settings as Tesserafin playback truth. Separate repeat/crossfade/output-device settings from PCM parameters.

A proposed configuration transaction contains:

- Host schema version, pinned engine/ABI identity, monotonically increasing sequence and playback epoch.
- Enabled intent, EQ enabled/ten gains, preamp, balance, mono, loudness and listening-volume input.
- Normalization owner (`legacy-web`, `processor`, `baked-server`, `none` or `unknown`), selected mode, media/album identity, gain dB and optional linear peaks.
- Actual format and requested wide-PCM policy, validated against capabilities.

Parse/validate JSON and choose media-dependent curves off the render thread. Queue a bounded typed transaction; one processing owner applies all its scalar values between blocks and acknowledges the sequence/epoch before the next eligible block. Scalar setters alone are not a cross-thread atomic transaction. Coalesce redundant UI edits; do not enqueue an unbounded message for every pointer movement. Drop obsolete-epoch messages.

OneAmp `DspParams` has no host schema/version field. `AudioSettings` version 1 is a separate document and includes player features. Neither replaces this host contract. Reject unsupported host versions without overwriting stored intent; present disabled/unavailable state and retain normal playback.

Expose a non-secret effective-state snapshot:

| Field | Meaning |
| --- | --- |
| status | `disabled`, `preparing`, `active`, `bypassed`, `unavailable` or `error` |
| reason | Stable reason such as `user-disabled`, `unsupported-format`, `encoded-output`, `origin-not-processable`, `insecure-context`, `engine-load-failed` |
| engine | Pinned revision/build hash and host adapter version |
| applied | Sequence/epoch and effective controls, distinct from requested controls |
| format | Actual input/output rate, channel order and block capacity |
| gain | Normalization owner/source, requested/effective dB if known; unknown remains unknown |
| timing | DSP latency frames and rate; browser/device latency separately, if available |
| health | Bounded counters for rejected blocks, resets and fallback transitions |

Do not expose URLs, credentials, raw media samples or identifying media paths in diagnostics. Rust `active()` can allocate a fold-down label; do not query/serialize it each render block. Bindings currently lack a full snapshot: derive only acknowledged host facts and exported values, never invent limiter meters or actual gain telemetry.

## Lifecycle

| Operation | Required host action |
| --- | --- |
| Prepare | Preflight availability, origin, format and asset identity. Allocate/compile/construct outside live rendering. Connect only after a usable bypass route exists. A failed prepare preserves ordinary playback. |
| Begin media | Create a new epoch; set explicit gain metadata/context, including clearing unknown figures; reset history before new PCM. Never inherit the last track's gain. |
| Process | One owner, bounded whole-frame blocks, preallocated buffers, validated counts; apply queued controls between blocks. |
| Disable/bypass | Route around the whole processor while preserving the host's established normalization and volume. Flat EQ or `is_bypassed` still includes limiter delay and is not literal zero-latency bypass. |
| Seek/discontinuity | Gate new PCM; discard old queued blocks/control, increment epoch, reset chain and acknowledge before resuming. Discard the previous tail; do not drain it into the seek destination. |
| Natural EOS | Gate normal input; flush once into scratch sized for at least latency × input channels; use returned output sample count, play the delayed tail, then reset. Native flush is not an incremental repeat-until-zero API or a guarantee of an infinite IIR decay. |
| Rate/layout/mode change | Stop admitting old-format PCM. Rust configure allocates; bindings need handle recreation. Prepare off callback and swap at a stream boundary. Rebuild for mode changes while G4 remains unresolved. |
| Pause/resume | Preserve history for the same continuous epoch unless the host declares discontinuity. AudioContext suspension/resume follows browser gesture rules. |
| Teardown | Gate callbacks and commands, disconnect owned nodes, free once after in-flight calls finish; do not close shared context/source resources still needed by ordinary playback. |

`Processor::reset()` clears chain history; it is not a parameter/media metadata reset and does not explicitly reset the separate track-gain ramp. Explicitly supply new media gain. Measure transition behavior before promising seamless track changes.

The host must serialize JNI processing and setters on one thread despite the Kotlin shared read lock. Never race free/configure/JSON parsing with a mutable native handle. Allocation-free Rust comments do not prove the complete JavaScript/JNI path is real-time safe; measure first-use, steady-state, control updates and lifecycle separately.

## Timing and end-of-stream accounting

The host media timeline stays authoritative. DSP does not change media duration, playback position or server delivery classification. Latency is a sample delay, not a new time origin; report it separately and compensate only once in the selected output synchronization layer.

With N input frames and latency L, fixed-delay processing emits N frames including initial delay; the final L frames require EOS drain. A harness must align output by L and verify the complete N-frame source content, including the last non-zero sample. Never drop the tail on the element's ended event or report completion early merely because input ended.

Do not retimestamp every network segment or subtract DSP latency from progress on every update. A future video adapter must validate lipsync, seek and output-device changes using the host presentation clock; a music-only demo cannot certify A/V sync.

## Conservative policy and fallback

Default the whole capability Off. When enabled initially: stereo only, EQ flat until explicitly edited, preamp 0 dB, balance centered, mono/loudness Off, OneAmp ReplayGain Off, OneAmp volume 1. Keep existing Web normalization and volume under their existing owner.

No automatic boosting from genre, scene or inferred loudness. Missing peak metadata never becomes a made-up peak of 1.0. Avoid doubling volume between element/gain node and DSP. Treat server measurements, baked filters and client normalization as distinct facts.

Before attachment, unsupported processing leaves the original player path usable. After a media element is bound to Web Audio, disconnecting the source alone does not restore direct element output: retain a source → legacy gain → destination bypass route. No second source for the same element. On worklet failure, route through this known graph, preserve position/session, emit a reason and stop retry loops. Test audible continuity and event ordering; if recovery needs a replacement element, make that an explicitly tested transition.

## Required validation before support claims

These are proposed acceptance gates, not measurements completed by DSP-1.

| Material | Gate / evidence to retain |
| --- | --- |
| Silence, impulse, low-level stereo sine | Finite output; measured delay equals exported latency; aligned flat output absolute error ≤ 1e-6 for the f32 harness |
| Full-scale transients, correlated tones, extreme supported EQ/preamp | Finite samples; sample peak ≤ 1 + 1e-6; report any limiter overshoot rather than relabeling it true-peak compliant |
| Gain-tag fixtures, missing track/album/peak fields, mixed vs album queue | Correct units/selected mode; one gain application; no stale gain on media change; preserve existing Web fallback selection |
| Short clips, final impulse, variable block partitions | Correct returned counts; no NaN, lost/duplicated content or missing EOS tail; test 64/128/256/512-frame glue |
| Seek, stop/start, rapid edits, failed worklet | No old-epoch samples; no half-applied controls; stable bypass and session/progress behavior |
| Per-speaker impulses and correlated wide material | Validate exact matrix, channel order and clipping before any multichannel claim; excluded initially |
| Audio/video clap/flash fixtures and seeks | Record baseline/processed A/V offset and drift with device/context latency; required before video support, deferred initially |
| Render profiling | Record hardware, browser, rate and block size, allocations/GC, deadline misses and control bursts; no zero-allocation claim from source alone |

Ship capability-specific evidence, including unsupported paths and fallback tests, rather than treating a single green upstream workflow as platform qualification.
