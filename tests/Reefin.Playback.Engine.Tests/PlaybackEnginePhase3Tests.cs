using System.Collections.Generic;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Engine.Tests;

/// <summary>
/// Acceptance tests for PR103 ("full engine semantics"): requested-source scoping, legacy-parity
/// subtitle default/forced auto-selection, invalid-index characterization, first-viable output
/// profile selection, and the bitrate ceiling cascade (request-wide, output-profile, per-codec).
/// </summary>
public static class PlaybackEnginePhase3Tests
{
    // --- Requested source scoping (acceptance tests 1-2) ---

    [Fact]
    public static void Decide_RequestedSourcePresentButNotViable_DoesNotFallBackToOtherSource()
    {
        // source-0: requested explicitly, but its video codec (av1) is undecodable and transcoding
        // is disallowed for this request - not viable. source-1 would direct-play cleanly, but it
        // was never requested, so the engine must not silently substitute it.
        var requested = EngineTestFixtures.Source(
            "source-0",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "av1")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var otherwiseViable = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var context = EngineTestFixtures.Context(MediaKind.Video) with { MediaSourceId = "source-0" };

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            context,
            EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]),
            [requested, otherwiseViable],
            EngineTestFixtures.Constraints(allowTranscoding: false));

        Assert.False(decision.IsViable);
        Assert.Contains(ReasonCode.VideoCodecNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.DoesNotContain(ReasonCode.RequestedSourceNotFound, FlattenReasonCodes(decision.Reasoning));
    }

    [Fact]
    public static void Decide_RequestedSourceIdNotFound_ReturnsRequestedSourceNotFound()
    {
        var onlySource = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var context = EngineTestFixtures.Context(MediaKind.Video) with { MediaSourceId = "does-not-exist" };

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            context,
            EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]),
            [onlySource],
            EngineTestFixtures.Constraints());

        Assert.False(decision.IsViable);
        Assert.Equal(string.Empty, decision.SelectedSource);
        Assert.Contains(ReasonCode.RequestedSourceNotFound, FlattenReasonCodes(decision.Reasoning));
        Assert.Contains(ReasonCode.NoViablePlan, FlattenReasonCodes(decision.Reasoning));
    }

    // --- Subtitle default/forced auto-selection parity (acceptance tests 3-4) ---

    [Fact]
    public static void Decide_ForcedSubtitleAutoSelected_WithoutExplicitIndex()
    {
        // Mode Default: "load subtitles according to external, default and forced flags"
        // (MediaStreamSelector.GetDefaultSubtitleStreamIndex, MediaStreamSelector.cs:55-59). The
        // only candidate is forced (not default, not external) - it is still auto-selected with no
        // PreferredSubtitleStreamIndex given, closing the PR101 oracle-documented gap.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "srt", isForced: true, language: "eng")]);

        var capabilities = EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]) with
        {
            Decode = EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]).Decode with
            {
                SubtitleDelivery = [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
            },
        };

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(subtitleMode: SubtitlePlaybackMode.Default));

        Assert.True(decision.IsViable);
        Assert.Equal(2, decision.SelectedStreams.Subtitle?.Index);
    }

    [Fact]
    public static void Decide_OnlyForcedMode_SelectsForcedStreamOverUnrelatedDefaultStream()
    {
        // Mode OnlyForced: "load subtitles that are flagged forced ... or with an undefined
        // language" (MediaStreamSelector.cs:80-84), regardless of which stream is IsDefault - the
        // mode, not the flag alone, drives which axis the engine honors. Index 2 is IsDefault but
        // not forced; index 3 is forced but not default. OnlyForced must pick index 3.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams:
            [
                EngineTestFixtures.SubtitleStream(2, "srt", isDefault: true, language: "eng"),
                EngineTestFixtures.SubtitleStream(3, "srt", isForced: true, language: "eng"),
            ]);

        var capabilities = EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]) with
        {
            Decode = EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]).Decode with
            {
                SubtitleDelivery = [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
            },
        };

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(subtitleMode: SubtitlePlaybackMode.OnlyForced, preferredSubtitleLanguages: ["eng"]));

        Assert.True(decision.IsViable);
        Assert.Equal(3, decision.SelectedStreams.Subtitle?.Index);
    }

    [Fact]
    public static void Decide_SubtitleModeNone_NeverAutoSelects()
    {
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "srt", isDefault: true, isForced: true, language: "eng")]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]),
            [source],
            EngineTestFixtures.Constraints(subtitleMode: SubtitlePlaybackMode.None));

        Assert.True(decision.IsViable);
        Assert.Null(decision.SelectedStreams.Subtitle);
    }

    // --- Invalid index characterization (acceptance test 5) ---

    [Fact]
    public static void Decide_InvalidPreferredAudioIndex_FallsBackToDefaultStream()
    {
        // Mirrors MediaSourceManager.SetDefaultAudioStreamIndex's remembered-selection validity
        // check (MediaSourceManager.cs:541-551): an explicit index naming no real stream degrades to
        // "no preference," not "no audio" - SelectAudio falls through to the IsDefault stream.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            audioStreams:
            [
                EngineTestFixtures.AudioStream(1, "aac", isDefault: false),
                EngineTestFixtures.AudioStream(2, "aac", isDefault: true),
            ]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Audio),
            EngineTestFixtures.Capabilities(["mp4"], [], ["aac"]),
            [source],
            EngineTestFixtures.Constraints(preferredAudioStreamIndex: 99));

        Assert.True(decision.IsViable);
        Assert.Equal(2, decision.SelectedStreams.Audio);
    }

    [Fact]
    public static void Decide_InvalidPreferredSubtitleIndex_SelectsNoSubtitle()
    {
        // Characterized (PR103), not "fixed": mirrors legacy's StreamBuilder.BuildVideoItem
        // (StreamBuilder.cs:661), which takes options.SubtitleStreamIndex via ?? without
        // revalidating it against the source's real streams - a stale explicit index resolves to no
        // real subtitle stream object downstream, the same practical outcome asserted here.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "srt", isDefault: true, language: "eng")]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]),
            [source],
            EngineTestFixtures.Constraints(preferredSubtitleStreamIndex: 99));

        Assert.True(decision.IsViable);
        Assert.Null(decision.SelectedStreams.Subtitle);
    }

    // --- First viable output profile (acceptance tests 6-7) ---

    [Fact]
    public static void Decide_FirstOutputProfileNotViable_SecondViable_SecondChosen()
    {
        // First declared profile has an empty container - unusable regardless of preference order;
        // the second, viable profile must be chosen instead of the named legacy fallback.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles:
            [
                new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Http, string.Empty, ["h264"], ["aac"], null, null, null),
                new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Http, "webm", ["h264"], ["aac"], null, null, null),
            ]);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "av1")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal("webm", decision.Output.Container);
        Assert.DoesNotContain(ReasonCode.OutputProfileFallbackUsed, FlattenReasonCodes(decision.Reasoning));
    }

    [Fact]
    public static void Decide_DeclaredOutputProfileHasNoViableCodecs_FallsBackToNamedLegacyDefault()
    {
        // The one declared profile needs a video codec list to be usable for this video transcode,
        // but declares none - unlike the "no profile declared at all" case, a profile IS present,
        // just not viable, and the engine must still fall back rather than pick it anyway.
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [],
                VideoCodecs: [],
                AudioCodecs: [],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles:
            [
                new PlaybackOutputProfile(MediaKind.Video, StreamingProtocol.Http, "mp4", [], ["aac"], null, null, null),
            ]);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "vp9")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "flac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal("h264", decision.Output.VideoCodec);
        Assert.Equal("aac", decision.Output.AudioCodec);
        Assert.Contains(ReasonCode.OutputProfileFallbackUsed, FlattenReasonCodes(decision.Reasoning));
    }

    // --- Bitrate ceiling cascade (acceptance tests 8-9) ---

    [Fact]
    public static void Decide_RequestMaxBitrateBelowSourceBitrate_ForcesTranscodeWithCappedTotalBitrate()
    {
        // Video and audio are both otherwise direct-playable, but the request's global MaxBitrate is
        // below the source's own muxed bitrate - only a transcode can honor that, since Direct Play
        // and Remux both copy the source streams verbatim.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            bitrate: 20_000_000);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]),
            [source],
            EngineTestFixtures.Constraints(maxBitrate: 5_000_000));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.TranscodeVideo, decision.Transforms);
        Assert.Contains(ReasonCode.ContainerBitrateExceedsLimit, FlattenReasonCodes(decision.Reasoning));
        Assert.Equal(5_000_000, decision.Output.TotalBitrate);
    }

    [Fact]
    public static void Decide_OutputProfileBitrateCeilings_AppearInOutputSpec()
    {
        var capabilities = new ClientCapabilities(
            Decode: new DecodeCapabilities(
                DirectPlayProfiles: [],
                VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [], null, null)],
                AudioCodecs: [new AudioCodecCapability("aac", null, null, null, null)],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            OutputProfiles:
            [
                new PlaybackOutputProfile(
                    MediaKind.Video,
                    StreamingProtocol.Http,
                    "mp4",
                    ["h264"],
                    ["aac"],
                    MaxVideoBitrate: 4_000_000,
                    MaxAudioBitrate: 128_000,
                    MaxAudioChannels: null),
            ]);

        // vp9/flac force both axes to transcode, so both ceilings actually shape the output.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mkv",
            videoStreams: [EngineTestFixtures.VideoStream(0, "vp9")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "flac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal(4_000_000, decision.Output.VideoBitrate);
        Assert.Equal(128_000, decision.Output.AudioBitrate);
        Assert.Equal(4_128_000, decision.Output.TotalBitrate);
    }

    // Housekeeping acceptance test 10 (EngineVersion == 5) is pinned by
    // PlaybackEnginePhase2Tests.EngineVersion_IsFive - not duplicated here.

    private static IEnumerable<ReasonCode> FlattenReasonCodes(ReasonNode node)
    {
        yield return node.Code;

        foreach (var child in node.Children)
        {
            foreach (var code in FlattenReasonCodes(child))
            {
                yield return code;
            }
        }
    }
}
