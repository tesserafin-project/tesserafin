# #153-LTV-R1 — repairs to the five LTV-R0 independent-review findings

This is the third stage of the Live TV delivery stack. #153-LTV-S0 routed the transcode off the
server's own `[Authorize]`d `/LiveTv/LiveStreamFiles/**` route and onto `pipe:0`. #153-LTV-S1
carried the validated capability into the HLS manifest so `hls.js` could fetch fragments at all.
An independent review, LTV-R0, then raised five findings. This stage closes them.

## What each finding was, and what closes it

### 1. ffprobe still fetched `LiveStreamFiles` over HTTP, uncredentialed

S0 routed the **transcode**. It did not route the **probe**: `LiveStreamHelper.AddMediaInfoWithProbe`
opened `MediaSourceInfo.Path`, which for a live tuner source is that authorized route, with no
credential. R0 measured one GET and one 401 on every live channel, and the media source was
published with `Codec null` / `Index -1` on every stream — which is why direct-stream was
unselectable and an HLS transcode was forced.

`MediaInfoRequest` gains `DirectStreamReader`. `MediaSourceManager.OpenLiveStreamInternal` passes
the provider the tuner host just returned; `MediaEncoder.GetMediaInfo` probes it over `pipe:0` with
`DirectStreamPump`, the same class, ownership model and cancellation model the transcode uses.

**A piped probe carries no protocol option.** ffmpeg resolves protocol options against the *input's*
protocol and `pipe:` has none, so a surviving `-user_agent` makes it refuse the whole invocation and
exit 8 — the same exit code as the 401 it replaces, from a different cause. S0 hit this on the
transcode side. `-user_agent` and `-rtsp_transport` are both dropped on the pipe branch.

This was the **preferred** route in the mission, and it needed no ABI break: adding a property to an
existing public class is additive, `IMediaEncoder` is untouched, and the ABI gate confirms
`Tesserafin.Controller.dll` COMPATIBLE. The single authorized alternative was therefore never
invoked.

### 2. A segment file was resolved from `segmentId` alone

`GetHlsVideoSegmentLegacy` resolved the file it served as
`Path.Combine(transcodeFolderPath, segmentId + extension)`. `itemId` was decorated
`[SuppressMessage CA1801]` and never read; `playlistId` only picked a job to keep alive. The
transcode folder is flat, so every live job's segments sit side by side in it.

Ownership is now **derived from the live job list**. `TranscodingJob` records the item, the media
source it was started for, and a process-wide generation; `TranscodeManager` projects those as an
`HlsSegmentBinding` through a new, narrow `IHlsSegmentBindingRegistry`. The route:

1. resolves the job from server state — a missing binding is a **closed refusal**, with no fallback;
2. requires the route's `itemId` to be the job's item;
3. resolves the validated provenance, refusing a presented-but-unvalidated capability;
4. requires the capability's item, media source and play session to be the job's;
5. requires a caller-named media source, **when one is named**, to be the job's;
6. requires `segmentId` to carry the job's own playlist prefix — ffmpeg named it that way, so this
   is a server-side fact and it is what stops `segmentId` alone from naming a file;
7. canonicalises the path and compares its directory with the job's root;
8. only then opens the file.

### 3. The validated capability's provenance was undefended

R0's control M1 fed the propagator a fresh `Request.Query["playbackCapability"]` read instead of the
validated record and **nothing in the repository went red**.

`ValidatedPlaybackCapability` is now a typed **request feature**, carrying the scope and the
validation result as well as the bindings. It is retrieved by type from one `HttpContext`, so no
request can read another's and nothing survives the request — both asserted, not argued.

`PlaybackCapabilityProvenance.Resolve` now **refuses** a capability presented in the query that
nothing validated, where it previously ignored the presentation. That refusal is what separates
"reads the query" from "reads the feature": R0 established the two were otherwise behaviourally
identical, because the attribute refuses every invalid presentation before the action runs.

### 4. The S0 and S1 hostile controls could not be replayed

They ran from python harnesses outside the tree that hardcoded one worktree path and mutated it in
place. `ci/hostile-controls/manifest.json` and `ci/hostile-controls/run.py` are the repaired
version: 32 controls with their mutation hunks, expected tests, timeouts and properties, and a
runner that creates its own git worktree, runs serially, builds before every gate, and compares
`git write-tree` against the baseline after each control.

### 5. Fragments carried no `playSessionId` binding

`ValidateCapability` guards its play-session comparison with `if (demand.PlaySessionId is not null…)`,
and the segment route named none — so the check was skipped entirely and R0 reached a segment with a
capability minted under a play session the server had never issued. The route now demands
`playSessionId`, and the propagator writes it into every fragment uri and into the fMP4 init map,
taken from the validated feature. No web change: `hls.js` fetches whatever uris the playlist hands
it.

## Five narrowings a reviewer should read before the passes

1. **A caller-named media source is required only when named.** Requiring it unconditionally
   refused every durable-token client whose playlist was never propagated — measured on the rig:
   playlist 200, four segment uris, every fetch 401. The *capability's* binding is still compared
   with the job's unconditionally, null against non-null included, which is the downgrade the
   mission forbids.
2. **"binding omis → refus" is not implemented as refusing a correct capability for a missing url
   parameter.** The capability's own play session, from the validation result, is compared with the
   job's whether or not the url names one, so omission is not a downgrade — and refusing on absence
   would contradict a contract `MediaAuthorizationBoundaryTests` pins for eleven other routes.
3. **The binding's lifetime is exactly the job's.** The mission also asks it to survive "until the
   segments are cleaned up"; there is no such cleanup. `KillTranscodingJobs(…, p => false)`
   suppresses `DeletePartialStreamFiles` and a live stream's null `RunTimeTicks` means no
   `TranscodingSegmentCleaner` runs, so segment files persist indefinitely. They become
   **unreachable**, which is not the same as absent.
4. **Two consumers share one transcode.** That proves no unexpected second tuner open and no
   corruption under concurrency; it does **not** prove two independent `GetStream()` readers. That
   property is `LiveStreamConsumerIsolationTests`, and the `s0-share-one-stream` control reds on it.
5. **`OpenApiContractTests` is not claimed to have run green.** `openapi/openapi.json` is
   byte-identical to the frozen base and both controllers carry `[ApiExplorerSettings(IgnoreApi = true)]`;
   that is the proof. The CI log is summary-only and does not name the test, and LTV-R0 withdrew
   exactly that inference.
