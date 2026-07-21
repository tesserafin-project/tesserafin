namespace Tesserafin.Playback.Decision;

/// <summary>
/// A closed, stable, string-serialized reason code identifying a single leaf fact in a
/// <see cref="ReasonNode"/> tree. The constraint members mirror
/// <c>Tesserafin.Model.Session.TranscodeReason</c> one-to-one (that flags enum lists which walls a
/// legacy decision hit, without saying why); the positive/decision members and
/// <see cref="NoViablePlan"/> have no legacy equivalent.
/// </summary>
public enum ReasonCode
{
    /// <summary>
    /// The source container is not one the client can play.
    /// </summary>
    ContainerNotSupported,

    /// <summary>
    /// The video codec is not one the client can decode.
    /// </summary>
    VideoCodecNotSupported,

    /// <summary>
    /// The audio codec is not one the client can decode.
    /// </summary>
    AudioCodecNotSupported,

    /// <summary>
    /// The subtitle codec/format is not one the client can render.
    /// </summary>
    SubtitleCodecNotSupported,

    /// <summary>
    /// The audio track is stored externally to the source, which the client/method cannot use.
    /// </summary>
    AudioIsExternal,

    /// <summary>
    /// A secondary audio track is not supported.
    /// </summary>
    SecondaryAudioNotSupported,

    /// <summary>
    /// The source has more streams than the client/method allows.
    /// </summary>
    StreamCountExceedsLimit,

    /// <summary>
    /// The video codec profile is not one the client supports.
    /// </summary>
    VideoProfileNotSupported,

    /// <summary>
    /// The video range type (for example HDR10, Dolby Vision) is not one the client supports.
    /// </summary>
    VideoRangeTypeNotSupported,

    /// <summary>
    /// The video codec tag is not one the client supports.
    /// </summary>
    VideoCodecTagNotSupported,

    /// <summary>
    /// The video codec level exceeds what the client supports.
    /// </summary>
    VideoLevelNotSupported,

    /// <summary>
    /// The video resolution exceeds what the client supports.
    /// </summary>
    VideoResolutionNotSupported,

    /// <summary>
    /// The video bit depth exceeds what the client supports.
    /// </summary>
    VideoBitDepthNotSupported,

    /// <summary>
    /// The video framerate exceeds what the client supports.
    /// </summary>
    VideoFramerateNotSupported,

    /// <summary>
    /// The video rotation metadata is not one the client supports.
    /// </summary>
    VideoRotationNotSupported,

    /// <summary>
    /// The video's reference frame count exceeds what the client supports.
    /// </summary>
    RefFramesNotSupported,

    /// <summary>
    /// Anamorphic video is not supported by the client.
    /// </summary>
    AnamorphicVideoNotSupported,

    /// <summary>
    /// Interlaced video is not supported by the client.
    /// </summary>
    InterlacedVideoNotSupported,

    /// <summary>
    /// The audio channel count exceeds what the client supports.
    /// </summary>
    AudioChannelsNotSupported,

    /// <summary>
    /// The audio codec profile is not one the client supports.
    /// </summary>
    AudioProfileNotSupported,

    /// <summary>
    /// The audio sample rate exceeds what the client supports.
    /// </summary>
    AudioSampleRateNotSupported,

    /// <summary>
    /// The audio bit depth exceeds what the client supports.
    /// </summary>
    AudioBitDepthNotSupported,

    /// <summary>
    /// The container's overall bitrate exceeds the configured limit.
    /// </summary>
    ContainerBitrateExceedsLimit,

    /// <summary>
    /// The video bitrate exceeds what the client or a configured limit supports.
    /// </summary>
    VideoBitrateNotSupported,

    /// <summary>
    /// The audio bitrate exceeds what the client or a configured limit supports.
    /// </summary>
    AudioBitrateNotSupported,

    /// <summary>
    /// The video stream's technical information could not be determined.
    /// </summary>
    UnknownVideoStreamInfo,

    /// <summary>
    /// The audio stream's technical information could not be determined.
    /// </summary>
    UnknownAudioStreamInfo,

    /// <summary>
    /// Direct play was attempted but failed for a reason not otherwise categorized.
    /// </summary>
    DirectPlayError,

    /// <summary>
    /// A stream was evaluated and found copyable without re-encoding.
    /// </summary>
    StreamCopyable,

    /// <summary>
    /// A media source was selected as the one to play back.
    /// </summary>
    SourceSelected,

    /// <summary>
    /// A playback method was chosen as the final decision.
    /// </summary>
    MethodChosen,

    /// <summary>
    /// Subtitles must be burned into the video image as part of this decision.
    /// </summary>
    SubtitleBurnInRequired,

    /// <summary>
    /// A text subtitle stream was re-encoded from its source format into a different format the
    /// client can render, without burning it into the video image.
    /// </summary>
    SubtitleFormatConverted,

    /// <summary>
    /// The audio must be downmixed to fewer channels as part of this decision.
    /// </summary>
    DownmixRequired,

    /// <summary>
    /// The video must be tonemapped from HDR to SDR as part of this decision.
    /// </summary>
    TonemapRequired,

    /// <summary>
    /// No viable playback plan could be produced for the request.
    /// </summary>
    NoViablePlan,

    /// <summary>
    /// The client declared no <c>PlaybackOutputProfile</c> matching the requested media kind, so
    /// the engine fell back to its named legacy default transcoding target (PR102).
    /// </summary>
    OutputProfileFallbackUsed,

    /// <summary>
    /// The request named a specific <c>MediaSourceId</c>, but no source with that id was present
    /// on the item (PR103). Distinct from a requested source that exists but is not viable: that
    /// case carries the usual non-viability reasons scoped to the requested source instead, and the
    /// engine never falls back to a different source either way.
    /// </summary>
    RequestedSourceNotFound,
}
