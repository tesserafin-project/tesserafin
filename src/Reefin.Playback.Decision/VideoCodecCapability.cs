using System.Collections.Generic;

namespace Reefin.Playback.Decision;

/// <summary>
/// A video codec a client can decode, and the limits within which it can decode it.
/// </summary>
/// <param name="Codec">The normalized codec name (for example <c>"h264"</c>).</param>
/// <param name="Profiles">The codec profiles the client supports. Empty means no profile restriction is expressed.</param>
/// <param name="MaxLevel">The highest codec level the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxBitDepth">The highest bit depth the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="VideoRangeTypes">The video range types (for example <c>"SDR"</c>, <c>"HDR10"</c>) the client supports for this codec.</param>
public sealed record VideoCodecCapability(
    string Codec,
    IReadOnlyList<string> Profiles,
    double? MaxLevel,
    int? MaxBitDepth,
    IReadOnlyList<string> VideoRangeTypes);
