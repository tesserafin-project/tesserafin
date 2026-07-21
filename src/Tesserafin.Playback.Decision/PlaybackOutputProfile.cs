using System.Collections.Generic;

namespace Tesserafin.Playback.Decision;

/// <summary>
/// One target format the server can produce for this client when it must transcode, distinct from
/// what the client can already read as-is (<see cref="DecodeCapabilities"/>). A client can declare
/// several of these on <see cref="ClientCapabilities.OutputProfiles"/>; their list order is the
/// client's preference order, most preferred first - PR102 exists precisely so that order survives
/// from the legacy <c>TranscodingProfile</c> list all the way to the engine, instead of being
/// collapsed into a flat capability set and replaced by hardcoded server-side preferences.
/// </summary>
/// <param name="Type">Whether this profile targets audio-only or video output.</param>
/// <param name="Protocol">The streaming protocol this output is delivered over.</param>
/// <param name="Container">The target container for this output.</param>
/// <param name="VideoCodecs">
/// The target video codecs, in the client's preference order (index 0 = most preferred). Empty for
/// an audio-only profile (<see cref="Type"/> of <see cref="MediaKind.Audio"/>).
/// </param>
/// <param name="AudioCodecs">The target audio codecs, in the client's preference order (index 0 = most preferred).</param>
/// <param name="MaxVideoBitrate">The maximum video bitrate for this output, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxAudioBitrate">The maximum audio bitrate for this output, or <see langword="null"/> if unbounded/unknown.</param>
/// <param name="MaxAudioChannels">The maximum audio channel count for this output, or <see langword="null"/> if unbounded/unknown.</param>
public sealed record PlaybackOutputProfile(
    MediaKind Type,
    StreamingProtocol Protocol,
    string Container,
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioCodecs,
    int? MaxVideoBitrate,
    int? MaxAudioBitrate,
    int? MaxAudioChannels);
