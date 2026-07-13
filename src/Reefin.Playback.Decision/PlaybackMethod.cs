namespace Reefin.Playback.Decision;

/// <summary>
/// The method by which the engine decided to deliver a media source to the client.
/// </summary>
public enum PlaybackMethod
{
    /// <summary>
    /// The source is played back unmodified: no remuxing, no transcoding.
    /// </summary>
    DirectPlay,

    /// <summary>
    /// The source's streams are copied into a different output container without re-encoding.
    /// </summary>
    Remux,

    /// <summary>
    /// One or more streams are re-encoded into formats the client can play.
    /// </summary>
    Transcode,
}
