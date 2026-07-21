namespace Tesserafin.Playback.Shadow;

/// <summary>
/// A pipeline transformation, normalized to a common vocabulary shared by the legacy (derived,
/// best-effort, from <c>TranscodeReason</c> + <c>PlayMethod</c>) and v2
/// (<c>Tesserafin.Playback.Decision.TransformKind</c>) representations. Mirrors
/// <c>Tesserafin.Playback.Decision.TransformKind</c> minus <c>CopyVideo</c>/<c>CopyAudio</c>, which carry
/// no comparable signal on the legacy side.
/// </summary>
public enum TransformClass
{
    /// <summary>
    /// The source container is remuxed into a different output container; streams are copied, not
    /// re-encoded.
    /// </summary>
    Remux,

    /// <summary>
    /// The video stream is re-encoded to a different codec, resolution, or video range.
    /// </summary>
    TranscodeVideo,

    /// <summary>
    /// The audio stream is re-encoded to a different codec or channel layout.
    /// </summary>
    TranscodeAudio,

    /// <summary>
    /// The audio channel count is reduced (for example, 5.1 down to stereo).
    /// </summary>
    Downmix,

    /// <summary>
    /// HDR video is converted to SDR.
    /// </summary>
    Tonemap,

    /// <summary>
    /// A subtitle track is burned into the video image.
    /// </summary>
    BurnInSubtitle,

    /// <summary>
    /// A subtitle track is extracted for external or embedded delivery.
    /// </summary>
    ExtractSubtitle,

    /// <summary>
    /// A text subtitle stream is re-encoded from one format to another for external/embedded delivery.
    /// </summary>
    ConvertSubtitle,
}
