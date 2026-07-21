namespace Tesserafin.Playback.Decision;

/// <summary>
/// The transport protocol an output stream is delivered over. Deliberately neutral vocabulary
/// (not <c>Tesserafin.Data.Enums.MediaStreamProtocol</c> or any other legacy/DLNA type): the domain
/// depends on nothing outside itself (RFC PR91 §4).
/// </summary>
public enum StreamingProtocol
{
    /// <summary>
    /// Plain HTTP progressive delivery.
    /// </summary>
    Http,

    /// <summary>
    /// HTTP Live Streaming delivery.
    /// </summary>
    Hls,
}
