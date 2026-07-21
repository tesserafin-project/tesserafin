namespace Tesserafin.Playback.Decision;

/// <summary>
/// How a subtitle stream is delivered to the client.
/// </summary>
public enum SubtitleDeliveryMethod
{
    /// <summary>
    /// The subtitle track is embedded in the output container.
    /// </summary>
    Embed,

    /// <summary>
    /// The subtitle track is delivered as a separate external file/sidecar.
    /// </summary>
    External,

    /// <summary>
    /// The subtitle track is burned into the video image.
    /// </summary>
    Burn,

    /// <summary>
    /// The subtitle track is delivered as an HLS subtitle rendition.
    /// </summary>
    Hls,
}
