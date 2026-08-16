namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// The kind of media a playback capability may fetch.
/// </summary>
/// <remarks>
/// A capability carries a SET of these and every protected route demands exactly one member. The
/// set exists because <see cref="Fonts"/> is not item-scoped: a fallback font belongs to no item
/// and has no media source, so a font capability carries no item binding at all. Collapsing the
/// set into a single "media" flag would either deny fonts or force a font capability to name an
/// item it has nothing to do with, and the second is how a narrow credential quietly becomes a
/// wide one.
/// </remarks>
public enum PlaybackCapabilityScope
{
    /// <summary>
    /// Primary media bytes: direct stream, container stream, universal audio, and every HLS
    /// playlist and segment. Item-bound.
    /// </summary>
    Media = 0,

    /// <summary>
    /// Subtitle streams and the subtitle playlist. Item-bound.
    /// </summary>
    Subtitles = 1,

    /// <summary>
    /// Container attachments. Item-bound.
    /// </summary>
    Attachments = 2,

    /// <summary>
    /// Trickplay tiles and the trickplay playlist. Item-bound.
    /// </summary>
    Trickplay = 3,

    /// <summary>
    /// Fallback fonts. NOT item-bound — see the remarks on this enum.
    /// </summary>
    Fonts = 4
}
