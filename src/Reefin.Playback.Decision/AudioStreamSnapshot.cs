namespace Reefin.Playback.Decision;

/// <summary>
/// A frozen snapshot of one audio stream on a <see cref="MediaSourceSnapshot"/>, decoupled from any
/// probing/model type.
/// </summary>
/// <param name="Index">The stream index within the source.</param>
/// <param name="Codec">The normalized codec name (for example <c>"aac"</c>).</param>
/// <param name="Channels">The channel count, or <see langword="null"/> if unknown.</param>
/// <param name="SampleRate">The sample rate in Hz, or <see langword="null"/> if unknown.</param>
/// <param name="BitDepth">The bit depth, or <see langword="null"/> if unknown.</param>
/// <param name="Bitrate">The stream bitrate, or <see langword="null"/> if unknown.</param>
/// <param name="Language">The stream's language tag, or <see langword="null"/> if unknown.</param>
/// <param name="IsDefault">Whether this is the default audio stream on the source.</param>
public sealed record AudioStreamSnapshot(
    int Index,
    string Codec,
    int? Channels,
    int? SampleRate,
    int? BitDepth,
    int? Bitrate,
    string? Language,
    bool IsDefault);
