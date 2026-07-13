using System.Collections.Generic;

namespace Reefin.Playback.Decision;

/// <summary>
/// Everything the v2 engine needs to know about a requesting client: what it can decode as-is
/// (<see cref="Decode"/>), and separately, what the server should produce when it must transcode
/// (<see cref="OutputProfiles"/>).
/// </summary>
/// <remarks>
/// PR102: v2.0/v2.1 modeled only <see cref="Decode"/>, and the DLNA adapter collapsed each
/// device's ordered <c>TranscodingProfile</c> list into that same flat decode surface. The engine
/// then had no client-declared transcoding target to select from, so it fell back to hardcoded
/// preferences (h264/aac/mp4/ts) - which is why a client declaring AV1 as its preferred transcode
/// target (for example Firefox's HLS/MP4 <c>TranscodingProfile</c>, which lists
/// <c>"av1,h264,vp9"</c>) was still handed H.264, and why direct-play-vs-remux was sometimes
/// misclassified. Splitting the two facets restores the client's real declared preference order
/// end to end: adapter -> domain -> engine.
/// </remarks>
/// <param name="Decode">What the client can read without any server-side transformation.</param>
/// <param name="OutputProfiles">
/// What the server should produce when it must transcode, in the client's preference order (index
/// 0 = most preferred). Empty when the client declares no transcoding targets at all, in which case
/// the engine falls back to a named legacy default (see <c>PlaybackEngine</c>).
/// </param>
public sealed record ClientCapabilities(
    DecodeCapabilities Decode,
    IReadOnlyList<PlaybackOutputProfile> OutputProfiles);
