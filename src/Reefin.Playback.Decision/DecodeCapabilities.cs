using System.Collections.Generic;

namespace Reefin.Playback.Decision;

/// <summary>
/// What a client can read as-is, expressed without DLNA vocabulary: declared direct-play
/// combinations, and the codecs/subtitle delivery decodable outside of those combinations, each
/// with the limits within which it is decodable. An inline, immutable snapshot: the full
/// capability set travels with the request instead of being referenced by a stored profile id, so
/// a decision is reproducible from the request alone (RFC PR91 §4, option A).
/// </summary>
/// <remarks>
/// Distinct from <see cref="PlaybackOutputProfile"/> (PR102): this describes decode-only
/// capability - what the client can play without any server-side transformation. It says nothing
/// about what the server should produce when it must transcode; that is
/// <see cref="ClientCapabilities.OutputProfiles"/>.
/// </remarks>
/// <remarks>
/// PR102b: <see cref="DirectPlayProfiles"/> replaces the old flat <c>Containers</c> list -
/// container acceptance is only meaningful in combination with a codec (see
/// <see cref="DecodeProfile"/>). Likewise the old global <c>MaxResolution</c>/
/// <c>MaxVideoBitrate</c>/<c>MaxAudioBitrate</c> fields are gone: those limits are properties of a
/// specific codec (an H.264-limited-to-1080p client is not thereby limited to 1080p for HEVC), so
/// they now live on <see cref="VideoCodecCapability"/>/<see cref="AudioCodecCapability"/>.
/// </remarks>
/// <param name="DirectPlayProfiles">
/// The direct-play combinations the client declares, in declared order. Distinct from
/// <see cref="VideoCodecs"/>/<see cref="AudioCodecs"/>: those describe per-codec decode limits
/// (used regardless of container), while this describes which container+codec(s) combinations are
/// actually playable together without transcoding (RFC PR102b problem #1).
/// </param>
/// <param name="VideoCodecs">The video codecs the client can decode, and their limits.</param>
/// <param name="AudioCodecs">The audio codecs the client can decode, and their limits.</param>
/// <param name="SubtitleDelivery">The subtitle formats the client can render, and how it wants them delivered.</param>
/// <param name="SupportsHls">Whether the client can play HLS renditions.</param>
/// <param name="SupportsDash">Whether the client can play DASH renditions.</param>
public sealed record DecodeCapabilities(
    IReadOnlyList<DecodeProfile> DirectPlayProfiles,
    IReadOnlyList<VideoCodecCapability> VideoCodecs,
    IReadOnlyList<AudioCodecCapability> AudioCodecs,
    IReadOnlyList<SubtitleCapability> SubtitleDelivery,
    bool SupportsHls,
    bool SupportsDash);
