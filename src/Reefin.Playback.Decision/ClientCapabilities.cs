using System.Collections.Generic;

namespace Reefin.Playback.Decision;

/// <summary>
/// What a client can read, expressed without DLNA vocabulary. An inline, immutable snapshot: the
/// full capability set travels with the request instead of being referenced by a stored profile
/// id, so a decision is reproducible from the request alone (RFC PR91 §4, option A).
/// </summary>
/// <param name="Containers">The containers (muxes) the client accepts.</param>
/// <param name="VideoCodecs">The video codecs the client can decode, and their limits.</param>
/// <param name="AudioCodecs">The audio codecs the client can decode, and their limits.</param>
/// <param name="SubtitleDelivery">The subtitle formats the client can render, and how it wants them delivered.</param>
/// <param name="MaxResolution">The maximum resolution the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxVideoBitrate">The maximum video bitrate the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxAudioBitrate">The maximum audio bitrate the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="SupportsHls">Whether the client can play HLS renditions.</param>
/// <param name="SupportsDash">Whether the client can play DASH renditions.</param>
public sealed record ClientCapabilities(
    IReadOnlyList<string> Containers,
    IReadOnlyList<VideoCodecCapability> VideoCodecs,
    IReadOnlyList<AudioCodecCapability> AudioCodecs,
    IReadOnlyList<SubtitleCapability> SubtitleDelivery,
    Resolution? MaxResolution,
    int? MaxVideoBitrate,
    int? MaxAudioBitrate,
    bool SupportsHls,
    bool SupportsDash);
