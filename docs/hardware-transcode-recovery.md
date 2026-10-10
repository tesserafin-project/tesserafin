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
- `IHardwareTranscodeFallback.GetEffectiveEncodingOptions(mediaSourceId)` (implemented by the
  transcode manager, kept off `ITranscodeManager` so that published interface does not change)
  returns a *copy* of the
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

A segment request on a play session whose transcode failed is answered

```
HTTP/1.1 410 Gone
X-Tesserafin-Playback-Recovery: software | none
Cache-Control: no-store
```

`software`: reloading the stream will be served by a software transcode. `none`: it will not
help. The body names no path, command or credential. 410 rather than 5xx because an HLS
client retries a 5xx on the same URL.

Which requests: after a failure that software takes over from, every one - nothing the failed
attempt wrote is served. After a failure with no fallback, the segments the attempt had finished
are still served, and the answer above is given for the one it was writing when it ended and for
everything after it. "Finished" means the attempt had gone on to the next file; that the process
has exited does not make its last file complete (see POLISH-2-R2 below).

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

## POLISH-2-R1: concurrent starts, and a recovery that belongs to one playback

Two things found while proving the above, and what was done about them. Same rig, same FFmpeg
7.1.4 (SHA-256 `ef565fc0…79a1`), same backend; nothing here is claimed for another one.

### Several playbacks starting together (tesserafin#286)

The first use of the transcode manager after a server start empties the transcode directory,
`.tesserafin-transcode` marker included. Every playback start checks the directory and recreates
the marker when it is missing; it did so with a truncating, exclusive open, so of several
starts arriving together all but one failed on the first one's handle and their
`master.m3u8` answered **500**.

**It predates #119.** Measured, not inferred, on three builds, always on a server that had not
transcoded since it started and always reading the *first* answer (a playback that starts after
a 500 because the client asked again is counted as a failure):

| Build | 6 playbacks by HTTP, released together | 4 browser contexts pressing Play together |
| --- | --- | --- |
| `b2cb895c0d`, the parent of #119 | 5 of 6 answered 500, in each of 3 rounds | 3 of 4 answered 500, in each of 3 rounds |
| `70a8974c48`, with #119 | 5 of 6, in each of 3 rounds | 3 of 4, in each of 3 rounds |
| the fix | 0, in 7 rounds | 0, in 4 rounds |

On a directory that already holds its marker, no build failed. Two or four requests for the
*same* item, from different devices, each got their own play session and their own output files.
The rounds are few on purpose: before the fix the failure was not a matter of luck, it happened
every time.

The marker is an empty file whose name is its whole content, so it is now opened shared and
without truncation: whoever comes second opens the file the first one made. Nothing is caught,
nothing is retried, no lock is taken. A directory that cannot be written, a full volume or a
directory sitting in the marker's place still throws, the check that refuses a directory
carrying another root's marker is unchanged, and so is the order in which a pre-rename marker is
replaced.

### One recovery, for one playback (web client)

The player object outlives the film it plays, and a recovery reload is several requests long.
The reload was guarded by flags on that object, which the next play request reset - so an
answer that arrived late was taken for a live one. Reproduced with the reload's answer held
back, on the unmodified client:

- after the 30 s recovery timeout had shown its error, the late answer started the film anyway;
- after film A was stopped and film B started, A's late answer replaced B's stream;
- a failure notified more than once started one reload per notification;
- a second `software` offer, after the one recovery had been used, reloaded again;
- B started without stopping A reported A as stopped at position zero.

The same happened to the ordinary retry ladder and to any other stream change (a track or
quality switch, a seek that reloads): their answers were no better guarded.

Now each playback is an object, made by the play request and gone when that playback is
reported stopped or is replaced, and the recovery in flight is another. Every asynchronous step
of a stream change carries the ones it started under; a step that finds different ones on the
player releases the transcode it was for and changes nothing else. Stop, timeout, replacement
and terminal failure all end a recovery through the same function. A stop asked for while a
stream change is in flight is honoured, and reported at the position the viewer was at.

**The signal is read strictly.** An instruction is `410` together with `software` or `none`,
exactly as the server writes them. A 410 with no header, with one the client cannot read, or
with any other value is an ordinary HTTP error: the client does not claim the server diagnosed
a failed transcode, and the ordinary retry ladder handles it. `software` is honoured once per
playback; any further recognised instruction ends that playback with the error dialog. `none`
starts no software recovery.

Proven by tests that drive the real playback manager with held answers and a controlled clock
(21 of the 35 fail on the unmodified client), and on the rig with the final pair, the recovery's
own request held in the browser:

| Check | Result |
| --- | --- |
| hardware failure mid-film, as above | recovered in 1.0 s, position +0.44 s, same audio and subtitles, one `libx264` start, resume saved at 156.2 s (failure at 143.7 s) |
| answer held past the 30 s timeout, then released | the error dialog, once; nothing started afterwards, no process left; resume saved at 135.1 s (failure at 130.7 s) |
| viewer leaves while the answer is held, then released | no player, no dialog, no process started, also after the old timer's time has passed; resume kept |
| viewer leaves, starts another film, then A's answer is released | B plays on for 42 s without a stall, a dialog or a change of source; no request for A's stream; A's resume kept |
| software attempt failing too | `410 software`, one reload, `410 none`, the dialog; Retry works |
| unreadable input on healthy hardware | `410 none` three times (see below), no software start, the dialog |

**`SIGKILL` of the hardware ffmpeg, watched to the end.** The dead job had written 176 segments
and the player was at segment 23. The viewer was moved to within 30 s of the last one and left
to play. When the player asked for segment 177, which the dead job never wrote, the server
started a new `h264_vaapi` transcode at that point and answered 200. Nothing was visible: no
stall, no notice, no dialog. The kill was "not recognised", no software fallback was granted,
the backend was not withheld and the next playback started on hardware. Not exercised: reaching
the hole within 30 s of the kill, while the dead job still answers 410.

**An automated second read** of both diffs (another Claude agent, no shared context - not a
human review) found nothing blocking on the server and one blocking defect on the client: a
stream that finished loading after its playback had been replaced stopped the player under the
next film. It and five smaller findings were repaired; a second read of the repairs found two
more, also repaired. The last repairs were not read a third time.

### What R1 leaves as it found it

- **`none` still goes down the ordinary retry ladder.** On unreadable input the client reloads
  twice more before the dialog, each a hardware attempt that fails at once. It is bounded and it
  is what the client did before; it is not a software recovery.
- **The ladder reloads once per error it is told about.** Only the software recovery ignores a
  repeated notification.
- **A hardware failure can reach the player as something other than a 410.** Seen once, not
  reproduced in two attempts: the device was lost while the player was waiting for a segment the
  job had not written yet, the server started a software transcode on the *same* play session a
  few milliseconds after the failed one ended, and the browser choked on software segments
  following a hardware initialisation segment. The ladder then reloaded and the film played, in
  software, without the notice and with three software starts instead of one. The cause is
  believed to be two checks in the segment route that are not atomic with the deletion of the
  failed job's files; that is an inference from one log. **Resolved in POLISH-2-R2, below.**
- **One unrelated 500 was seen under concurrent starts**, in 1 of 18 browser rounds on the fixed
  server and none of the HTTP ones: `SQLite Error 5: unable to delete/modify collation sequence
  due to active statements`, on a segment request and a progress report 2.5 s after four films
  were started together. The client reloaded that one stream. Whether it predates #119 is not
  known - the older builds failed earlier, on the marker.
- A live stream opened for a stream change whose answer arrives too late is released like any
  other transcode, by play session; this was not exercised with a tuner.

## POLISH-2-R2: a failing attempt and the requests that meet it (tesserafin#289)

R1 left one observation unexplained: a hardware failure that reached the player as software
segments on the *same* play session instead of a 410. It was a race, and it was wider than the one
log suggested.

A segment request decides between serving a file, saying the attempt failed, and starting a
transcode. It decided on two looks at the disk and one at the job, while the failing job removed
its files first and published why afterwards. Held open on purpose in tests - the real controller
action, the real transcode manager, a real child process, every interleaving stopped at a gate
rather than by a delay - the unfixed code did three things, in 12 of 20 cases:

| Interleaving | Before |
| --- | --- |
| the request had seen its segment, or found a healthy attempt, or taken the lock, or decided on a seek - and then the attempt failed and removed its output | a second transcode on the same output and play session, in **hardware** again: the options had been read before the failure withheld it |
| the request arrived while the output was being removed | **HTTP 500**, `IOException: Broken pipe`: the process was gone and nothing said so yet |
| the last segment of a failed attempt, the one it was writing | served 200 - live, to a request that was waiting for it, and after the answer had expired |

What holds now:

- **Published before removed.** The failure, then "has exited", then the removal - under the
  output's own lock, the one a request holds while it starts a transcode there. A file found
  missing because of the removal always comes with its reason; a start on that output is entirely
  before the removal or entirely after it.
- **Removed once, by whoever holds that lock first**: the failed attempt's own exit, or the start
  of a successor when the client released the play session first. A successor never starts on
  what the failed attempt left, and a late exit leaves the successor's files alone.
- **Asked in that order, and asked again**: file first, attempt second; before the lock, under it,
  and once more before an attempt is stopped to make room for another. No lock is taken by a
  request that only reads, nothing is retried, nothing waits.
- **Failed is not finished** - the rule stated under "What the client is told".
- The 30 seconds are unchanged. Past them the same play session starts afresh, as before, after
  deleting the failed attempt's last file instead of leaving it to be served.

**On the rig**, same FFmpeg 7.1.4 (SHA-256 `ef565fc0…79a1`), same backend, final pair (this change
with web `af1d4ef1ef`). Unpaced, the hardware encoder finishes a 20-minute film in about 15 s and
the player is never near the edge of what is written, so the encoder was paced at the same
boundary the fault is injected at (a sleep before each command submission, in that ffmpeg process
only) until the server was *holding* the player's segment request. The fault came then.

| Run | Result |
| --- | --- |
| at the edge after three +30 s seeks, 4 runs: the server holding the request for segment 31, the last and unfinished one on disk, 2.2 s buffered ahead | that request answered `410 software`; one `PlaybackInfo`, one play session in software on a new output with its own initialisation segment, no hardware start, no `bufferAppendError`, no fatal player error; the notice shown; advancing again after 1.5 s at +0.21 to +0.28 s; resume saved at 114.6 to 115.5 s (failure at 95.9 s) |
| the same with French audio and French subtitles selected first, two seeks | the same; `-map 0:0 -map 0:2` before and after, the French cue before and after; 1.5 s, +0.29 s |
| encoder far ahead of the player (fault at 257.9 s, segment 88 the first missing) | `410 software`, one software play session, 1.5 s, +0.63 s, same tracks, resume saved at 271.8 s |
| two sessions, both at the edge, one fault | each held request answered `410 software`; one software start each on its own new output; both advancing 4.4 s after the fault |
| viewer leaves on the first failed response | no player, no dialog, no process started after the fault, none left running; resume saved at 62.9 s (viewer was at 61.0 s) |
| the software attempt fails too (input made unreadable with the fault) | `410 software`, one software start, `410 none` on its initialisation segment, the dialog; Retry plays once the input is back |
| unreadable input on healthy hardware | `410 none` three times (the ordinary ladder), no software start, the dialog; Retry plays |
| `SIGKILL` of the hardware ffmpeg, its last segment (8) on disk and unfinished | 11 s later the player asks for segment 8: `410 none`, where it used to be served; the ladder reloads once, a new hardware play session, no dialog |

Tolerances as in POLISH-2: position within 3 s, recovery within 15 s, same tracks, one software
start. The two command lines a software play session shows (segment 0, then `-ss`) are one play
session on one output, as in every earlier run.

**What the browser did not show.** The same edge runs on the unfixed server also recovered cleanly,
8 times of 8: the window is a few milliseconds and the browser does not find it on demand. The
defect is reproduced by the tests, where it is not a matter of luck; the browser shows what the
viewer gets with the fix, not the defect without it. The one occurrence in the field is R1's.

**An automated read** of the candidate (another Claude agent, no shared context - not a human
review) found nothing blocking and seven things to improve; six were taken, among them that the
removal is now the exit handler's last step, so that a request holding the lock cannot keep a
failed job from being reported ended. The repairs were not read again.

### What R2 leaves

- A seek that stops an attempt in the few milliseconds between its verdict and its publication
  starts another on the same play session, possibly in hardware once more, beside what the failed
  one wrote. It is told at its next failure.
- A segment chosen for serving before the failure was published, and removed before it is opened,
  answers 404; the next request gets the 410.
- ~~The delete a client's release schedules 1.5 s later is not serialized with starts.~~ Serialized in POLISH-2-R3, below.
- After the 30 seconds, a client that never reloaded gets a fresh transcode on the same play
  session while still holding the old initialisation segment, as before.
- A process killed without a recognisable line (`SIGKILL`) is still refused a fallback. Its last
  segment is no longer served: the player gets `410 none` there and the ordinary ladder reloads the
  stream. Before, a new transcode took over silently on the same play session.
- The SQLite failure of R1 (tesserafin#288) is a separate defect; see the issue.

## POLISH-2-R3: the neighbours of that fix

R2 changed the output's lock and who removes an output when, and proved it on one route. R3 went
to the others with the same tests. Nothing here is tesserafin#289, and no tuner was involved: the
live route was driven with a file, as the tests drive every route.

| Found | Now |
| --- | --- |
| a live playlist request waiting for its first segments held the output's lock for ever when the transcode ended by itself - and since R2, a client's stop could no longer end the wait | the wait ends when the process does; a failure is answered 410 with the recovery header |
| a live playlist whose attempt software takes over from was served while still on disk, then a second process was started in its place on the same play session | 410, before the lock and under it; nothing is started while the answer lasts |
| a live start that failed before its first output answered an unexplained 500 | 410, as on the segment route |
| past the 30 seconds a live restart left the failed job registered beside its successor | the failed one leaves first |
| the legacy segment routes (`Videos/{id}/hls/{playlist}/…`, `Audio/{id}/hls/…`), which a live playlist points at, served a failed attempt's files until the removal reached them | 410 when software takes over |
| the removal a release schedules 1.5 s later ran outside the lock and deleted the files of an attempt started meanwhile on the same play session | under the lock, and not at all when another attempt holds the output |
| the request for the initialisation segment that started an attempt was refused where the same request, repeated, was served | served |

Still as found: the legacy routes do not apply the "last file" rule after a failure with no
fallback; `Videos/{id}/hls/{playlist}/stream.m3u8` answers 400 to every request (its guard refuses
exactly the extension it serves) and was left alone; the 410 on the live routes has not been
exercised with a tuner or with a client playing live.

## Not covered

- Any backend other than VAAPI on AMD. V4L2 M2M commands open no device on the command line and
  are treated as software: they are never fallen back from.
- Progressive (non-HLS) transcodes: a withheld backend is honoured for new ones, but a
  failure of one is neither classified nor announced to the client.
- A hardware failure that leaves no recognisable line.
- Re-admitting a backend without a restart.
