namespace Reefin.Playback.Shadow;

/// <summary>
/// A category that folds together several related legacy <c>TranscodeReason</c> bits and v2
/// <c>ReasonCode</c> values, so a <see cref="DecisionVector"/> can compare "why" at a coarse,
/// stable granularity instead of by raw flag/code equality (which would fail on irrelevant
/// differences per docs/pr93-compatibility-lab.md §4.1).
/// </summary>
public enum ReasonCategory
{
    /// <summary>
    /// The container is not supported by the client.
    /// </summary>
    Container,

    /// <summary>
    /// The video codec, profile, level, codec tag, reference frame count, rotation, anamorphic, or
    /// interlaced handling is not supported by the client.
    /// </summary>
    VideoCodec,

    /// <summary>
    /// The video range type (for example HDR10, Dolby Vision) is not supported by the client.
    /// </summary>
    VideoRange,

    /// <summary>
    /// The video resolution, bit depth, or framerate is not supported by the client.
    /// </summary>
    VideoDims,

    /// <summary>
    /// The audio codec or profile is not supported by the client.
    /// </summary>
    AudioCodec,

    /// <summary>
    /// The audio channel count is not supported by the client.
    /// </summary>
    AudioChannels,

    /// <summary>
    /// The audio sample rate or bit depth is not supported by the client.
    /// </summary>
    AudioRate,

    /// <summary>
    /// The video, audio, or container bitrate exceeds a configured or client limit.
    /// </summary>
    Bitrate,

    /// <summary>
    /// The subtitle codec/format is not supported by the client.
    /// </summary>
    Subtitle,

    /// <summary>
    /// The source has more streams (secondary audio, external audio) than the client/method allows.
    /// </summary>
    StreamCount,

    /// <summary>
    /// Stream information could not be determined, or direct play failed for an uncategorized reason.
    /// </summary>
    Error,
}
