namespace Tesserafin.Playback.Shadow;

/// <summary>
/// A playback method normalized to a common vocabulary shared by the legacy
/// (<c>Tesserafin.Model.Session.PlayMethod</c>) and v2 (<c>Tesserafin.Playback.Decision.PlaybackMethod</c>)
/// representations, so a <see cref="DecisionVector"/> can compare them directly.
/// </summary>
public enum NormalizedMethod
{
    /// <summary>
    /// The source is played back unmodified: no remuxing, no transcoding.
    /// </summary>
    DirectPlay,

    /// <summary>
    /// The source's streams are copied into a (possibly different) output container without
    /// re-encoding. Legacy's <c>PlayMethod.DirectStream</c> maps to this value.
    /// </summary>
    Remux,

    /// <summary>
    /// One or more streams are re-encoded into formats the client can play.
    /// </summary>
    Transcode,
}
