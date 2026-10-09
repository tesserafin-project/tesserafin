namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// How far a software fallback reaches once <see cref="TranscodeFallbackPlanner"/> has granted it.
/// </summary>
public enum TranscodeFallbackScope
{
    /// <summary>No fallback was granted.</summary>
    None,

    /// <summary>
    /// The failure is about this input or this capability only. Software is used for this media
    /// source; the backend stays available to everything else.
    /// </summary>
    MediaSource,

    /// <summary>
    /// The failure positively identifies the device or backend as unable to work. It is withheld
    /// from new selections for the rest of this server process.
    /// </summary>
    Backend,
}
