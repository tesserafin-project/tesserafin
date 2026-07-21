namespace Tesserafin.Playback.Decision;

/// <summary>
/// The kind of entity a <see cref="ReasonNode"/> is reasoning about.
/// </summary>
public enum ReasonSubjectKind
{
    /// <summary>
    /// The source or output container.
    /// </summary>
    Container,

    /// <summary>
    /// A specific video stream.
    /// </summary>
    VideoStream,

    /// <summary>
    /// A specific audio stream.
    /// </summary>
    AudioStream,

    /// <summary>
    /// A specific subtitle stream.
    /// </summary>
    Subtitle,

    /// <summary>
    /// A media source as a whole.
    /// </summary>
    Source,

    /// <summary>
    /// The overall playback method.
    /// </summary>
    Method,
}
