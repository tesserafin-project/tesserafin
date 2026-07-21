namespace Tesserafin.Playback.Shadow;

/// <summary>
/// How a subtitle stream is delivered to the client, normalized to a common vocabulary shared by
/// the legacy (<c>Tesserafin.Model.Dlna.SubtitleDeliveryMethod</c>) and v2
/// (<c>Tesserafin.Playback.Decision.SubtitleDeliveryMethod</c>) representations, mirroring the pattern
/// established by <see cref="NormalizedMethod"/> and <see cref="TransformClass"/>. Includes
/// <see cref="None"/> - unlike the two domain enums it normalizes, a <see cref="DecisionVector"/>
/// needs to say "no subtitle was selected" directly, since that is exactly the axis PR101 exists to
/// compare reliably (see <see cref="StreamSelection"/>).
/// </summary>
public enum SubtitleDeliveryMode
{
    /// <summary>
    /// No subtitle was selected.
    /// </summary>
    None,

    /// <summary>
    /// The subtitle track is embedded in the output container. Legacy's <c>Embed</c> maps here.
    /// </summary>
    Embed,

    /// <summary>
    /// The subtitle track is delivered as a separate external file/sidecar. Legacy's <c>External</c>
    /// maps here.
    /// </summary>
    External,

    /// <summary>
    /// The subtitle track is burned into the video image. Legacy's <c>Encode</c> maps here.
    /// </summary>
    Burn,

    /// <summary>
    /// The subtitle track is delivered as an HLS subtitle rendition. Legacy's <c>Hls</c> maps here.
    /// </summary>
    Hls,
}
