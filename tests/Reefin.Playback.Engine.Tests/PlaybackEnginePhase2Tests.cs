using System.Collections.Generic;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Engine.Tests;

/// <summary>
/// Boundary unit tests for the phase-2 (PR97) engine additions: transcoding, resolution/level/
/// bit-depth/channel limits, HDR tonemapping, subtitle delivery/burn-in, and the
/// transcode-disallowed no-viable-plan path. Each test pins an edge (at the limit vs. one past it)
/// that the fixture-parity tests sample but don't isolate as precisely.
/// </summary>
public static class PlaybackEnginePhase2Tests
{
    [Fact]
    public static void Decide_VideoLevelAtMax_IsNotTripped()
    {
        var decision = DecideWithVideoLevel(level: 41);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.DoesNotContain(TransformKind.TranscodeVideo, decision.Transforms);
    }

    [Fact]
    public static void Decide_VideoLevelOneOverMax_TripsLevelCheck()
    {
        var decision = DecideWithVideoLevel(level: 42);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.TranscodeVideo, decision.Transforms);
        Assert.Contains(ReasonCode.VideoLevelNotSupported, FlattenReasonCodes(decision.Reasoning));
    }

    private static PlaybackDecision DecideWithVideoLevel(double level)
    {
        var capabilities = new ClientCapabilities(
            Containers: ["mp4"],
            VideoCodecs: [new VideoCodecCapability("h264", [], MaxLevel: 41, null, [])],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null)],
            SubtitleDelivery: [],
            MaxResolution: null,
            MaxVideoBitrate: null,
            MaxAudioBitrate: null,
            SupportsHls: false,
            SupportsDash: false);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264", level: level)],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        return engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());
    }

    [Fact]
    public static void Decide_AudioChannelsAtEffectiveMax_Copies()
    {
        var decision = DecideWithAudioChannels(channels: 2);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.DoesNotContain(TransformKind.Downmix, decision.Transforms);
    }

    [Fact]
    public static void Decide_AudioChannelsOneOverEffectiveMax_Downmixes()
    {
        var decision = DecideWithAudioChannels(channels: 3);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.TranscodeAudio, decision.Transforms);
        Assert.Contains(TransformKind.Downmix, decision.Transforms);
        Assert.Contains(ReasonCode.AudioChannelsNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.Equal(2, decision.Output.AudioChannels);
    }

    private static PlaybackDecision DecideWithAudioChannels(int channels)
    {
        // effMaxChannels = min(cap.MaxChannels=6, constraints.MaxAudioChannels=2) = 2.
        var capabilities = new ClientCapabilities(
            Containers: ["mp4"],
            VideoCodecs: [],
            AudioCodecs: [new AudioCodecCapability("aac", MaxChannels: 6, null, null)],
            SubtitleDelivery: [],
            MaxResolution: null,
            MaxVideoBitrate: null,
            MaxAudioBitrate: null,
            SupportsHls: false,
            SupportsDash: false);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            audioStreams: [EngineTestFixtures.AudioStream(0, "aac", isDefault: true, channels: channels)]);

        var engine = new PlaybackEngine();
        return engine.Decide(
            EngineTestFixtures.Context(MediaKind.Audio),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(maxAudioChannels: 2));
    }

    [Fact]
    public static void Decide_ResolutionAtMax_IsNotTripped()
    {
        var decision = DecideWithResolution(width: 1920, height: 1080);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Null(decision.Output.Resolution);
    }

    [Fact]
    public static void Decide_WidthOneOverMax_Downscales()
    {
        var decision = DecideWithResolution(width: 1921, height: 1080);

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.TranscodeVideo, decision.Transforms);
        Assert.Contains(ReasonCode.VideoResolutionNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.Equal(new Resolution(1920, 1080), decision.Output.Resolution);
    }

    private static PlaybackDecision DecideWithResolution(int width, int height)
    {
        var capabilities = new ClientCapabilities(
            Containers: ["mp4"],
            VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [])],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null)],
            SubtitleDelivery: [],
            MaxResolution: new Resolution(1920, 1080),
            MaxVideoBitrate: null,
            MaxAudioBitrate: null,
            SupportsHls: false,
            SupportsDash: false);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264", width: width, height: height)],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        return engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());
    }

    [Fact]
    public static void Decide_SubtitleAlreadyExternal_DeliversExternalWithNoExtractAndStaysDirectPlay()
    {
        var capabilities = new ClientCapabilities(
            Containers: ["mp4"],
            VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [])],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null)],
            SubtitleDelivery: [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
            MaxResolution: null,
            MaxVideoBitrate: null,
            MaxAudioBitrate: null,
            SupportsHls: false,
            SupportsDash: false);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "srt", isExternal: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(preferredSubtitleStreamIndex: 2));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Empty(decision.Transforms);
        Assert.Equal(SubtitleDeliveryMethod.External, decision.SelectedStreams.Subtitle?.Delivery);
        Assert.DoesNotContain(TransformKind.ExtractSubtitle, decision.Transforms);
    }

    [Fact]
    public static void Decide_SubtitleUnsupportedFormat_BurnsInAndTranscodes()
    {
        var capabilities = new ClientCapabilities(
            Containers: ["mp4"],
            VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [])],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null)],
            SubtitleDelivery: [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
            MaxResolution: null,
            MaxVideoBitrate: null,
            MaxAudioBitrate: null,
            SupportsHls: false,
            SupportsDash: false);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "pgssub")]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(preferredSubtitleStreamIndex: 2));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.BurnInSubtitle, decision.Transforms);
        Assert.Equal(SubtitleDeliveryMethod.Burn, decision.SelectedStreams.Subtitle?.Delivery);
        Assert.Contains(ReasonCode.SubtitleCodecNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.Contains(ReasonCode.SubtitleBurnInRequired, FlattenReasonCodes(decision.Reasoning));
    }

    [Fact]
    public static void Decide_AlwaysBurnInWhenTranscoding_AddsBurnInForTextSubtitleOnUnrelatedVideoTranscode()
    {
        // Video already needs transcoding for an unrelated reason (codec not supported at all).
        // The subtitle itself would otherwise deliver cleanly as an External text format, but
        // AlwaysBurnInSubtitleWhenTranscoding overrides that once a video transcode is already
        // happening.
        var capabilities = new ClientCapabilities(
            Containers: ["mp4"],
            VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [])],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null)],
            SubtitleDelivery: [new SubtitleCapability("srt", SubtitleDeliveryMethod.External)],
            MaxResolution: null,
            MaxVideoBitrate: null,
            MaxAudioBitrate: null,
            SupportsHls: false,
            SupportsDash: false);

        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "av1")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)],
            subtitleStreams: [EngineTestFixtures.SubtitleStream(2, "srt", isExternal: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(preferredSubtitleStreamIndex: 2, alwaysBurnInSubtitleWhenTranscoding: true));

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(TransformKind.BurnInSubtitle, decision.Transforms);
        Assert.Equal(SubtitleDeliveryMethod.Burn, decision.SelectedStreams.Subtitle?.Delivery);
    }

    [Fact]
    public static void Decide_TranscodeDisallowedButNeeded_IsNotViable()
    {
        var capabilities = new ClientCapabilities(
            Containers: ["mp4"],
            VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [])],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null)],
            SubtitleDelivery: [],
            MaxResolution: null,
            MaxVideoBitrate: null,
            MaxAudioBitrate: null,
            SupportsHls: false,
            SupportsDash: false);

        // av1 is not decodable by the client at all, so a viable plan needs a video transcode -
        // but this request disallows transcoding outright.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "av1")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            capabilities,
            [source],
            EngineTestFixtures.Constraints(allowTranscoding: false));

        Assert.False(decision.IsViable);
        Assert.Equal(string.Empty, decision.SelectedSource);
        Assert.Contains(ReasonCode.VideoCodecNotSupported, FlattenReasonCodes(decision.Reasoning));
        Assert.Contains(ReasonCode.NoViablePlan, FlattenReasonCodes(decision.Reasoning));
    }

    [Fact]
    public static void Decide_VideoSourceWithNoStreamsAtAll_IsNotViable_DoesNotThrow()
    {
        // A MediaKind.Video source with empty VideoStreams and empty AudioStreams has nothing for
        // any method to select, copy, or transcode. Phase 1 fell through to NotViable here (the
        // videoOk/audioOk gates could never be satisfied); phase 2 must too, rather than crash
        // trying to build a StreamCopyable reason with no selected stream to reference.
        var capabilities = new ClientCapabilities(
            Containers: ["mp4"],
            VideoCodecs: [new VideoCodecCapability("h264", [], null, null, [])],
            AudioCodecs: [new AudioCodecCapability("aac", null, null, null)],
            SubtitleDelivery: [],
            MaxResolution: null,
            MaxVideoBitrate: null,
            MaxAudioBitrate: null,
            SupportsHls: false,
            SupportsDash: false);

        var source = EngineTestFixtures.Source("source-1", "mkv");

        var engine = new PlaybackEngine();
        var decision = engine.Decide(EngineTestFixtures.Context(MediaKind.Video), capabilities, [source], EngineTestFixtures.Constraints());

        Assert.False(decision.IsViable);
        Assert.Equal(string.Empty, decision.SelectedSource);
    }

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
