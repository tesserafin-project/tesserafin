# DSP-2: bounded Web stereo-music demonstrator

Status: proposed implementation scope following [DSP-1 / #308](https://github.com/tesserafin-project/tesserafin/issues/308), tracked in [tesserafin-web #211](https://github.com/tesserafin-project/tesserafin-web/issues/211). Implementation begins after review of the [audit](oneamp-dsp-1-audit.md) and [host contract](oneamp-host-contract.md). This document does not activate processing.

## User outcome and boundary

An explicitly enabled experimental equalizer processes an eligible local music track while the existing Tesserafin Web player continues to own transport, progress, queue, normalization and volume. Turning it off or failing to initialize leaves playback usable.

Target `tesserafin-web`'s `htmlAudioPlayer` only. Start with known stereo sources, progressive Direct Play, same-origin media, secure contexts and actual AudioContext rates 44.1/48 kHz. Do not treat forced stereo node output as proof of the source layout. Unknown formats are ineligible.

Exclude video, multichannel, mono-source adaptation, remux/transcode/HLS qualification, remote URLs, cast/receivers, offload/bitstream, Android, automatic EQ selection, loudness, ReplayGain migration and crossfade/gapless claims. They remain separate slices with their own evidence.

## Implementation tasks

1. **Pinned asset build:** select the audited OneAmp commit or a reviewed successor; compile PCM-only WASM with locked dependencies. Add reproducible build instructions, content hashes, notices and a dependency inventory. Self-host assets under the existing Web asset/CSP pipeline. Record any upstream fixes rather than silently tracking main.
2. **One audio graph owner:** retain/reuse the element's AudioContext, source and legacy normalization gain; introduce explicit ownership and cleanup. Eligible enabled graph: source → existing normalization gain → worklet → destination. Disabled/error graph: source → existing normalization gain → destination. Preserve user-volume behavior, including Safari. Avoid two source nodes or two normalizers.
3. **Bounded worklet adapter:** do not ship the upstream helper unchanged. Chunk variable render blocks into preallocated supported sizes; validate native output counts; never de-interleave unproduced samples. Use bounded scalar transactions between blocks, with sequence/epoch acknowledgements. Keep JSON encoding and live handle creation off the rendering path. Add reset, EOS/drain and disposal protocols.
4. **Lifecycle integration:** preflight before graph attachment, gate old/new PCM around seek, clear per-media metadata, preserve pause/resume and natural EOS tail, rebuild for format changes outside live rendering. Recover processor errors through the retained bypass graph. Prove the input-ended signal and drain acknowledgement preserve player ended/progress ordering; do not wire a late drain onto an already completed session.
5. **Minimal opt-in and state:** default Off, explicit experimental enable/disable plus ten-band EQ and preamp only. Preserve existing normalization UI/settings; OneAmp ReplayGain Off and volume 1. Expose requested/applied status and a concise unsupported/failure reason. No account entitlement or hosted-service dependency.
6. **Evidence:** add native/WASM vectors, glue tests and real-browser runs below. Update support claims only for paths actually tested.

## Acceptance and review gates

- No processing asset download or graph activation on the default-disabled path unless required by an explicitly enabled persisted setting.
- WASM load/CSP failure, insecure context, unsupported format and worklet error preserve playback and session/progress; no credentials or media URLs appear in diagnostics.
- Flat/EQ/preamp vectors cover the host contract's finite-output, latency and clipping checks. Include short tracks and final impulses to prove EOS drain.
- Worklet tests cover at least 64, 128, 256 and 512 frames, native short-count rejection, missing inputs, burst controls, repeated reset/dispose and stale epochs. Retain the DSP-1 mock probe as audit evidence, not as proof of actual WASM correctness.
- Confirm normalization is applied once for Off/TrackGain/AlbumGain, metadata fallback and media changes. Test element-volume and Safari gain behavior before declaring Safari supported.
- Real Chromium and Firefox runs: same-origin local fixtures, user-gesture resume, seek/pause, track changes, toggle/error fallback and ended/progress ordering. Add WebKit/Safari measurements before advertising those engines.
- Retain engine/adapter hashes, rates, hardware/browser versions and render profiling. Offline or no-account local playback remains usable.
- No changes to server delivery policy, authentication/credential routing, debt-reduction refactors or Android playback are required by this demonstrator.

If EOS ordering, graph recovery or actual sink behavior cannot meet these gates in a bounded client implementation, stop at the failing gate and revise the design with evidence. Do not substitute a second player or a server transcode requirement.

## Why Web first; what unblocks Android later

Web has an existing music player and gain graph; PCM-only WASM and its Node harness already build upstream. The worklet gaps are explicit and can be contained in one client wrapper.

Android needs a native playback slice first, a pinned Media3 sink proof (including integer/float conversion and PCM/offload routing), a DSP-only JNI package, bounded buffer semantics, direct-buffer EOS support or a measured copy strategy, native packaging checks and physical-device profiling. None is established by the current JNI example. Continue Android's own delivery sequence independently; this discovery PR does not make DSP a prerequisite.
