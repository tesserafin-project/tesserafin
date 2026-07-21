namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The kind of media a <see cref="PlaybackSessionRequest"/> is planning for.
/// </summary>
public enum PlaybackMediaKind
{
    /// <summary>Audio-only playback.</summary>
    Audio,

    /// <summary>Video (with optional audio/subtitle) playback.</summary>
    Video,
}
