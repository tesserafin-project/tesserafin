using System.Collections.Generic;

namespace Tesserafin.Playback.Decision;

/// <summary>
/// A video codec a client can decode, and the limits within which it can decode it.
/// </summary>
/// <remarks>
/// PR102b: <see cref="MaxResolution"/> and <see cref="MaxBitrate"/> moved here from the old global
/// <c>DecodeCapabilities.MaxResolution</c>/<c>MaxVideoBitrate</c> fields - those limits come from a
/// legacy <c>CodecProfile</c>'s <c>Width</c>/<c>Height</c>/<c>VideoBitrate</c> conditions, which are
/// themselves scoped to the codec(s) the profile applies to. A client limiting H.264 to 1080p and
/// HEVC to 2160p was previously collapsed to a single global 1080p ceiling (the minimum of the
/// two); each codec now carries its own limit.
/// </remarks>
/// <param name="Codec">The normalized codec name (for example <c>"h264"</c>).</param>
/// <param name="Profiles">The codec profiles the client supports. Empty means no profile restriction is expressed.</param>
/// <param name="MaxLevel">The highest codec level the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxBitDepth">The highest bit depth the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="VideoRangeTypes">The video range types (for example <c>"SDR"</c>, <c>"HDR10"</c>) the client supports for this codec.</param>
/// <param name="MaxResolution">The maximum resolution the client supports for this codec, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxBitrate">The maximum video bitrate the client supports for this codec, or <see langword="null"/> if unbounded/unknown.</param>
public sealed record VideoCodecCapability(
    string Codec,
    IReadOnlyList<string> Profiles,
    double? MaxLevel,
    int? MaxBitDepth,
    IReadOnlyList<string> VideoRangeTypes,
    Resolution? MaxResolution,
    int? MaxBitrate);
