namespace Reefin.Playback.Decision;

/// <summary>
/// The kind of media being requested for playback.
/// </summary>
public enum MediaKind
{
    /// <summary>
    /// Audio-only media.
    /// </summary>
    Audio,

    /// <summary>
    /// Video media (with or without accompanying audio).
    /// </summary>
    Video,
}
