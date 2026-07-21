using Tesserafin.Controller.Streaming;

namespace Tesserafin.Api.Models.StreamingDtos;

/// <summary>
/// The hls video request dto.
/// </summary>
public class HlsAudioRequestDto : StreamingRequestDto
{
    /// <summary>
    /// Gets or sets a value indicating whether enable adaptive bitrate streaming.
    /// </summary>
    public bool EnableAdaptiveBitrateStreaming { get; set; }
}
