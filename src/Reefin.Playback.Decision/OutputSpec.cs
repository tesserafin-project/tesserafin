namespace Reefin.Playback.Decision;

/// <summary>
/// The shape of the output a <see cref="PlaybackDecision"/> produces.
/// </summary>
/// <param name="Container">The normalized output container name, or <see langword="null"/> if not applicable.</param>
/// <param name="VideoCodec">The normalized output video codec, or <see langword="null"/> if not applicable.</param>
/// <param name="AudioCodec">The normalized output audio codec, or <see langword="null"/> if not applicable.</param>
/// <param name="Resolution">The output resolution, or <see langword="null"/> if not applicable/unchanged.</param>
/// <param name="VideoRange">The normalized output video range type, or <see langword="null"/> if not applicable/unchanged.</param>
/// <param name="AudioChannels">The output audio channel count, or <see langword="null"/> if not applicable/unchanged.</param>
/// <param name="TotalBitrate">
/// The output's overall bitrate ceiling (PR103), or <see langword="null"/> if not
/// applicable/unbounded. Populated only when transcoding, as the narrowest of
/// <see cref="PlaybackConstraints.MaxBitrate"/> (the request's global cap) and the sum of
/// <see cref="VideoBitrate"/>/<see cref="AudioBitrate"/> when both are known - never a fabricated
/// value out of unknown per-stream ceilings.
/// </param>
/// <param name="VideoBitrate">
/// The output video stream's bitrate ceiling (PR103), or <see langword="null"/> if not
/// applicable/unbounded. Populated only when the video is being transcoded, as the narrowest of the
/// used <see cref="PlaybackOutputProfile.MaxVideoBitrate"/> and the target codec's
/// <see cref="VideoCodecCapability.MaxBitrate"/>.
/// </param>
/// <param name="AudioBitrate">
/// The output audio stream's bitrate ceiling (PR103), or <see langword="null"/> if not
/// applicable/unbounded. Populated only when the audio is being transcoded, as the narrowest of the
/// used <see cref="PlaybackOutputProfile.MaxAudioBitrate"/> and the target codec's
/// <see cref="AudioCodecCapability.MaxBitrate"/>.
/// </param>
/// <param name="Protocol">
/// The transport protocol this output is delivered over (PR102b). For Direct Play and Remux,
/// always <see cref="StreamingProtocol.Http"/> - a protocol only diverges from plain HTTP when the
/// server produces the encoding, i.e. when transcoding to a client-declared
/// <see cref="PlaybackOutputProfile"/>. Non-nullable: <see cref="StreamingProtocol.Http"/> is the
/// neutral value for the "not applicable" case too, so this decision never leaves the delivery
/// protocol unstated.
/// </param>
public sealed record OutputSpec(
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    Resolution? Resolution,
    string? VideoRange,
    int? AudioChannels,
    int? TotalBitrate,
    int? VideoBitrate,
    int? AudioBitrate,
    StreamingProtocol Protocol)
{
    /// <summary>
    /// An output spec with no fields set, used for non-viable decisions.
    /// </summary>
    public static readonly OutputSpec Empty = new(null, null, null, null, null, null, null, null, null, StreamingProtocol.Http);
}
