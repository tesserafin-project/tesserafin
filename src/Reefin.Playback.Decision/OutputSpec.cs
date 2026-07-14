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
/// <param name="Bitrate">The output bitrate, or <see langword="null"/> if not applicable/unbounded.</param>
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
    int? Bitrate,
    StreamingProtocol Protocol)
{
    /// <summary>
    /// An output spec with no fields set, used for non-viable decisions.
    /// </summary>
    public static readonly OutputSpec Empty = new(null, null, null, null, null, null, null, StreamingProtocol.Http);
}
