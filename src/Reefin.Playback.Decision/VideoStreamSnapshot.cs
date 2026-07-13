namespace Reefin.Playback.Decision;

/// <summary>
/// A frozen snapshot of one video stream on a <see cref="MediaSourceSnapshot"/>, decoupled from any
/// probing/model type.
/// </summary>
/// <param name="Index">The stream index within the source.</param>
/// <param name="Codec">The normalized codec name (for example <c>"h264"</c>).</param>
/// <param name="Profile">The codec profile, or <see langword="null"/> if unknown.</param>
/// <param name="Level">The codec level, or <see langword="null"/> if unknown.</param>
/// <param name="Width">The width in pixels, or <see langword="null"/> if unknown.</param>
/// <param name="Height">The height in pixels, or <see langword="null"/> if unknown.</param>
/// <param name="BitDepth">The bit depth, or <see langword="null"/> if unknown.</param>
/// <param name="VideoRange">The video range type (for example <c>"SDR"</c>, <c>"HDR10"</c>), or <see langword="null"/> if unknown.</param>
/// <param name="Framerate">The framerate in frames per second, or <see langword="null"/> if unknown.</param>
/// <param name="Bitrate">The stream bitrate, or <see langword="null"/> if unknown.</param>
/// <param name="IsAnamorphic">Whether the stream is anamorphic.</param>
/// <param name="IsInterlaced">Whether the stream is interlaced.</param>
public sealed record VideoStreamSnapshot(
    int Index,
    string Codec,
    string? Profile,
    double? Level,
    int? Width,
    int? Height,
    int? BitDepth,
    string? VideoRange,
    double? Framerate,
    int? Bitrate,
    bool IsAnamorphic,
    bool IsInterlaced);
