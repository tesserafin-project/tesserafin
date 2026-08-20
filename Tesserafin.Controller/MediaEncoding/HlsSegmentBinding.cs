using System;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The server's own record of who owns a legacy HLS playlist's segment files (#153-LTV-R1).
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. <c>Videos/{itemId}/hls/{playlistId}/{segmentId}.{container}</c> resolved the
/// file it served as <c>Path.Combine(transcodeFolderPath, segmentId + extension)</c> — from the
/// caller-supplied <c>segmentId</c> alone. <c>itemId</c> was never read, and the transcode folder
/// is flat, so every live job's segments sit side by side in it and a capability that satisfied
/// one job reached another job's bytes. LTV-R0 named that finding 2.
///
/// The repair is to stop deriving ownership from the request at all. A transcoding job already
/// knows the item, the media source, the play session and the directory ffmpeg was told to write
/// into; this projects that knowledge so the route can compare against it instead of trusting the
/// parameters it was handed.
///
/// LIFETIME, STATED EXACTLY. A binding exists precisely while its job is active. There is no
/// separate store to fall out of step with the job list, and no fallback path once the job is
/// gone: the answer is then a closed refusal. #153-LTV-S0 measured that a live job's segment files
/// outlive it indefinitely — <c>KillTranscodingJobs(deviceId, playSessionId, p =&gt; false)</c>
/// suppresses <c>DeletePartialStreamFiles</c>, and a live stream's null <c>RunTimeTicks</c> means
/// no <c>TranscodingSegmentCleaner</c> ever runs — so those leftover files become unreachable
/// rather than being served to whoever asks for them by name.
/// </remarks>
/// <param name="PlaylistId">The job's own playlist identifier: the output file name without its extension. ffmpeg names every segment of the job with this as its prefix.</param>
/// <param name="ItemId">The item the job was started for.</param>
/// <param name="MediaSourceId">The media source the job was started for.</param>
/// <param name="PlaySessionId">The play session the job belongs to.</param>
/// <param name="CanonicalRoot">The canonicalized directory the job's files live in.</param>
/// <param name="CanonicalPlaylistPath">The canonicalized path of the job's own output playlist.</param>
/// <param name="Generation">A number that increases with every job the process starts, so a re-used playlist identifier is still distinguishable.</param>
public sealed record HlsSegmentBinding(
    string PlaylistId,
    Guid ItemId,
    string? MediaSourceId,
    string? PlaySessionId,
    string CanonicalRoot,
    string CanonicalPlaylistPath,
    long Generation);
