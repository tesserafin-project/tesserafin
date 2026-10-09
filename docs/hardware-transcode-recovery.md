# Recovering from a hardware transcode failure (tesserafin#119)

A4 (#90) verifies the hardware backend when the server starts. This is what happens when a
hardware transcode fails *after* that: the playback continues in software where the cause
justifies it, and later playbacks do not need a server restart.

Proven on **one backend only: VAAPI on an AMD GPU (Mesa radeonsi)**, with Tesserafin FFmpeg
7.1.4 and a Chromium-family browser. Nothing below is claimed for QSV, NVENC, AMF,
VideoToolbox, V4L2 or RKMPP: their run-time failure output has not been observed, so the
classifier recognises none of it and such a failure is refused, not guessed.

## What the viewer sees

| Situation | Before | Now |
| --- | --- | --- |
| The device stops answering before a playback's first segment | three hardware attempts, then "Playback failed due to a fatal player error." | one hardware attempt, then the film starts, in software |
| The device is lost while hardware segments are being played | the picture freezes when the hardware segments run out; no message, no way out | a notice ("…Resuming without it…"), then the film continues from the same position, same audio track and subtitles, in software |
| Any later playback, same server process | fails the same way until the server is restarted | starts in software directly |
| The software attempt fails too, or the failure is not a hardware one | a dead player or "fatal player error" | "The server could not convert this video for playback. Your position has been kept." with **Retry** and **Back** |

## The decision

`FfmpegErrorClassifier` assigns categories to lines of ffmpeg's stderr; `JobLogger` keeps
every category it saw for the attempt. When ffmpeg exits with a non-zero code,
`TranscodeManager` gives `TranscodeFallbackPlanner` what the attempt ran and what was seen:

| Evidence | Decision | Reaches |
| --- | --- | --- |
| the server stopped the process itself (stop, seek, navigation, cancellation, kill timer) | not a failure at all | – |
| the command that ran used no hardware device | refuse – already software | – |
| nothing recognised: a crash, a signal, a bare exit code | refuse | – |
| the input could not be opened or read – even beside a device failure | refuse | – |
| this ffmpeg has no software encoder for the output codec | refuse | – |
| the device could not be opened (`DeviceInitializationFailed`) | software | the backend, until restart |
| the driver reported a lost or rejected context under a running encode (`HardwareDeviceLost`) | software | the backend, until restart |
| "Unknown encoder/decoder" naming a codec of the backend in use (`h264_vaapi`, …) | software | that media source only |
| "Unknown encoder/decoder" naming anything else | refuse – software lacks it too | – |

A permission error on the render node is **not** distinguished from any other failure to
open it: ffmpeg prints `No VA display found` for both. `Permission denied` has only been
observed about an input file, and is treated as one.

`Unknown` is never a fallback. A hardware encode that is killed, or that segfaults inside
the driver (which is what refusing every DRM ioctl with `ENODEV` produces: exit 139 and no
line), leaves no evidence and is refused.

## What a granted fallback does

- **Nothing is restarted by the server.** The decision changes what the *next* command is
  built with. That is what bounds the incident to one software attempt: the client reloads the
  stream once, and there is no second mechanism to multiply it with.
- `ITranscodeManager.GetEffectiveEncodingOptions(mediaSourceId)` returns a *copy* of the
  configured options with hardware acceleration off when the backend, or that media source,
  has been withheld. `EncodingHelper` builds the whole command from those options, so the
  decoder, the filters and the upload/download steps are the software ones too – no encoder
  name is swapped inside a hardware command.
- The stored configuration is not modified and not saved. A restart re-verifies the backend
  (A4) and uses it again if it passes. There is no periodic re-probe.
- A session that is still healthy on hardware is not touched.
- Everything the failed attempt wrote is deleted. Its last segment may be truncated and its
  fMP4 initialisation segment belongs to another encoder; neither may reach the software
  attempt's viewer. The reload gets a new play session and so new output files.

## What the client is told

A request for a segment the failed job never produced is answered

```
HTTP/1.1 410 Gone
X-Tesserafin-Playback-Recovery: software | none
Cache-Control: no-store
```

`software`: reloading the stream will be served by a software transcode. `none`: it will not
help. The body names no path, command or credential. 410 rather than 5xx because an HLS
client retries a 5xx on the same URL.

This is an addition on the HLS segment routes, which are fetched by the media player and not
through the generated SDK; the OpenAPI document and the SDK are unchanged. A client that does
not know the header sees a failed segment, as before.

The failed job answers this way for 30 seconds, or until the client releases it
(`DELETE /Videos/ActiveEncodings`, which a reload already sends). After that a segment request
on the same play session is served a fresh transcode, built with the options then in force –
which is how a client that does not know the header still recovers, by asking again.

The header is listed in the CORS policy's exposed headers, so a web client served from another
origin can read it.

## How it was proven

Rig: a throwaway server and data directory, the official Tesserafin FFmpeg 7.1.4 (SHA-256
`ef565fc0…79a1`, equal to `ci/package/f0-accepted-digests.txt`), Google Chrome, product
defaults – the server selected VAAPI by itself at start.

**Fault injection, and its limit.** The server's `--ffmpeg` pointed at a two-line wrapper
that runs the official binary with one `LD_PRELOAD` library. While a trigger file exists,
that library makes `ioctl()` on DRM devices fail *inside that ffmpeg process only*: either
every ioctl with `ENODEV`, or command submission alone with `ECANCELED`, which is what
`amdgpu` answers for a context lost in a GPU reset. The render node, the driver and every
other program on the machine are untouched. The lines the classifier matches are printed by
the real libva and Mesa in reaction. It is a simulation at the system-call boundary: no GPU
was reset and none was unplugged.

Tolerances were written down before the validating runs: position within ±3 s (one 3 s
segment), recovery within 15 s, same tracks, exactly one software start.

| Check | Result |
| --- | --- |
| hardware segments actually played before the fault | segments 0–49, `h264_vaapi`, French audio selected |
| fault | lost context on command submission; ffmpeg printed the `amdgpu` line and aborted |
| first failed response → playback advancing again | 1.6 s (1.0 s in an earlier run) |
| position when failed → first advancing position | +0.65 s (+0.46 s) |
| audio track, subtitles after recovery | French audio (`-map 0:0 -map 0:2`), French subtitle cue – unchanged |
| software start | one, `libx264`, `-ss 00:02:24` |
| resume position saved after stopping | 156.8 s (failure at 143.8 s) |
| next playback, same process, fault still present | `libx264` from the first command, no error |
| device failing before the first segment | one `h264_vaapi` attempt, 410 `software`, `libx264`, playing ~1 s later |
| stored `HardwareAccelerationType` after all of it | `vaapi`; restart selected VAAPI again |
| software attempt also failing (input made unreadable) | 410 `none`, dialog with Retry / Back; Retry resumed at 258 s once readable |
| unreadable input on healthy hardware | 410 `none`, no software start |
| `SIGKILL` of the hardware ffmpeg | refused ("not recognised"); backend kept; next playback hardware |
| viewer leaves during recovery | no process started afterwards, none left running |
| two sessions on hardware, one fault | both recovered, one software start each |

## Second-pass review

A separate automated read of both diffs (another Claude agent, no shared context – **not an
independent human review**) found one blocking defect and eight smaller ones; all nine were
repaired before the final runs. On the server: the stop flag is set before anything that can
make ffmpeg exit; a verdict needs stderr to have been read to its end, otherwise it is a
refusal; the attempt's backend is taken from the command that ran, not from options that may
have changed since; the progressive and live-HLS command builders use the effective options
too; the failure answer expires; the header is exposed to other origins. Not repaired, by
choice: image extraction (`MediaEncoder`) still uses the stored options, and `Device creation
failed` also describes an OpenCL/Vulkan filter device, which withholds the whole backend.

## Not covered

- Any backend other than VAAPI on AMD. V4L2 M2M commands open no device on the command line and
  are treated as software: they are never fallen back from.
- Progressive (non-HLS) transcodes: a withheld backend is honoured for new ones, but a
  failure of one is neither classified nor announced to the client.
- A hardware failure that leaves no recognisable line.
- Re-admitting a backend without a restart.
