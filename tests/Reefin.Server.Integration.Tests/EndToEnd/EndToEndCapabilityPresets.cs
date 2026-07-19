using System.Collections.Generic;
using Reefin.Playback.Decision;

namespace Reefin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// PR119: builds the <see cref="ClientCapabilities"/>/<see cref="PlaybackConstraints"/> pairs that
/// deterministically steer the legacy <c>StreamBuilder</c> (via
/// <c>ReverseDlnaAdapter.ToDeviceProfile</c>/<c>ApplyConstraints</c>) to a specific
/// <c>PlayMethod</c> for the one real fixture <see cref="EndToEndMediaFixtures"/> produces
/// (H.264/AAC/MP4). Verified against <c>Reefin.Model/Dlna/StreamBuilder.cs</c>
/// (<c>BuildVideoItem</c>/<c>GetVideoDirectPlayProfile</c>):
/// <list type="bullet">
/// <item>DirectPlay: a declared direct-play combination whose container/codecs match the fixture
/// exactly, with <see cref="PlaybackConstraints.AllowDirectPlay"/> true.</item>
/// <item>Remux (DirectStream): a declared direct-play combination whose CODECS match but whose
/// CONTAINER deliberately does not (<c>"webm"</c> vs. the fixture's real <c>"mp4"</c>) - StreamBuilder's
/// <c>DirectStreamReasons</c> mask tolerates a container mismatch for DirectStream specifically
/// (remux = compatible codecs, different container), so this becomes DirectStream once
/// <see cref="PlaybackConstraints.AllowDirectPlay"/> is false and
/// <see cref="PlaybackConstraints.AllowDirectStream"/> is true.</item>
/// <item>Transcode (HLS): both direct methods forbidden by constraints, forcing
/// <c>GetVideoTranscodeProfile</c> to run against the declared HLS
/// <see cref="PlaybackOutputProfile"/>.</item>
/// </list>
/// </summary>
public static class EndToEndCapabilityPresets
{
    public const string FixtureContainer = "mp4";
    public const string FixtureVideoCodec = "h264";
    public const string FixtureAudioCodec = "aac";

    /// <summary>A container the fixture is deliberately NOT declared under, to force remux (codecs match, container does not).</summary>
    private const string MismatchedContainer = "webm";

    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) DirectPlay()
    {
        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, [FixtureContainer], [FixtureVideoCodec], [FixtureAudioCodec])],
            VideoCodecs: [new VideoCodecCapability(FixtureVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(FixtureAudioCodec, null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: true,
            SupportsDash: false);

        var capabilities = new ClientCapabilities(decode, OutputProfiles: []);
        var constraints = BaseConstraints(allowDirectPlay: true, allowDirectStream: true, allowTranscoding: true);

        return (capabilities, constraints);
    }

    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) Remux()
    {
        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, [MismatchedContainer], [FixtureVideoCodec], [FixtureAudioCodec])],
            VideoCodecs: [new VideoCodecCapability(FixtureVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(FixtureAudioCodec, null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: true,
            SupportsDash: false);

        var capabilities = new ClientCapabilities(decode, OutputProfiles: []);
        var constraints = BaseConstraints(allowDirectPlay: false, allowDirectStream: true, allowTranscoding: false);

        return (capabilities, constraints);
    }

    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) TranscodeHls()
    {
        // DirectPlayProfiles deliberately empty (no direct-play combination declared at all, so
        // DirectPlay is unreachable regardless of constraints) - VideoCodecs/AudioCodecs still
        // declared so PlaybackSessionRequestValidator's "declared nothing decodable" rule is
        // satisfied; they play no role in ruling DirectPlay in since DirectPlayProfiles stays empty.
        var decode = new DecodeCapabilities(
            DirectPlayProfiles: [],
            VideoCodecs: [new VideoCodecCapability(FixtureVideoCodec, [], null, null, [], null, null)],
            AudioCodecs: [new AudioCodecCapability(FixtureAudioCodec, null, null, null, null)],
            SubtitleDelivery: [],
            SupportsHls: true,
            SupportsDash: false);

        var outputProfiles = new List<PlaybackOutputProfile>
        {
            new(MediaKind.Video, StreamingProtocol.Hls, "ts", [FixtureVideoCodec], [FixtureAudioCodec], null, null, null),
        };

        var capabilities = new ClientCapabilities(decode, outputProfiles);

        // AllowVideoStreamCopy=false: forces a REAL re-encode (not just a remux-through-HLS) so this
        // scenario actually exercises TranscodeManager/ffmpeg, not merely a container change.
        var constraints = new PlaybackConstraints(
            AllowDirectPlay: false,
            AllowDirectStream: false,
            AllowTranscoding: true,
            AllowVideoStreamCopy: false,
            AllowAudioStreamCopy: true,
            MaxBitrate: null,
            MaxAudioChannels: null,
            PreferredAudioStreamIndex: null,
            PreferredSubtitleStreamIndex: null,
            SubtitleMode: SubtitlePlaybackMode.None,
            PreferredSubtitleLanguages: [],
            AlwaysBurnInSubtitleWhenTranscoding: false,
            StartTimeTicks: 0);

        return (capabilities, constraints);
    }

    /// <summary>
    /// DirectPlay preset augmented with an external subtitle declaration - the client declares it can
    /// render <c>srt</c> via external delivery, and explicitly prefers the given stream index (mirrors
    /// a real client that already knows which track it wants, rather than relying on
    /// auto-selection/scoring).
    /// </summary>
    public static (ClientCapabilities Capabilities, PlaybackConstraints Constraints) DirectPlayWithExternalSubtitle(int subtitleStreamIndex)
    {
        var (baseCapabilities, baseConstraints) = DirectPlay();
        var decode = baseCapabilities.Decode with
        {
            SubtitleDelivery = [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
        };
        var capabilities = baseCapabilities with { Decode = decode };
        var constraints = baseConstraints with { PreferredSubtitleStreamIndex = subtitleStreamIndex };

        return (capabilities, constraints);
    }

    private static PlaybackConstraints BaseConstraints(bool allowDirectPlay, bool allowDirectStream, bool allowTranscoding) => new(
        AllowDirectPlay: allowDirectPlay,
        AllowDirectStream: allowDirectStream,
        AllowTranscoding: allowTranscoding,
        AllowVideoStreamCopy: true,
        AllowAudioStreamCopy: true,
        MaxBitrate: null,
        MaxAudioChannels: null,
        PreferredAudioStreamIndex: null,
        PreferredSubtitleStreamIndex: null,
        SubtitleMode: SubtitlePlaybackMode.None,
        PreferredSubtitleLanguages: [],
        AlwaysBurnInSubtitleWhenTranscoding: false,
        StartTimeTicks: 0);
}
