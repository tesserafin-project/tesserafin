namespace Reefin.Playback.Decision;

/// <summary>
/// An audio codec a client can decode, and the limits within which it can decode it.
/// </summary>
/// <param name="Codec">The normalized codec name (for example <c>"aac"</c>).</param>
/// <param name="MaxChannels">The highest channel count the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxSampleRate">The highest sample rate (Hz) the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxBitDepth">The highest bit depth the client supports, or <see langword="null"/> if unbounded/unknown.</param>
public sealed record AudioCodecCapability(
    string Codec,
    int? MaxChannels,
    int? MaxSampleRate,
    int? MaxBitDepth);
