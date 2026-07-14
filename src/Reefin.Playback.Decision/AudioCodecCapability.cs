namespace Reefin.Playback.Decision;

/// <summary>
/// An audio codec a client can decode, and the limits within which it can decode it.
/// </summary>
/// <remarks>
/// PR102b: <see cref="MaxBitrate"/> moved here from the old global
/// <c>DecodeCapabilities.MaxAudioBitrate</c> field, for the same reason as
/// <see cref="VideoCodecCapability.MaxResolution"/>/<see cref="VideoCodecCapability.MaxBitrate"/> -
/// it is a per-codec limit in the legacy model, not a device-wide one.
/// </remarks>
/// <param name="Codec">The normalized codec name (for example <c>"aac"</c>).</param>
/// <param name="MaxChannels">The highest channel count the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxSampleRate">The highest sample rate (Hz) the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxBitDepth">The highest bit depth the client supports, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxBitrate">The maximum audio bitrate the client supports for this codec, or <see langword="null"/> if unbounded/unknown.</param>
public sealed record AudioCodecCapability(
    string Codec,
    int? MaxChannels,
    int? MaxSampleRate,
    int? MaxBitDepth,
    int? MaxBitrate);
