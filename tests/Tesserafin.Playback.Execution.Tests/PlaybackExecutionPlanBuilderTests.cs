using System;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Execution.Tests;

/// <summary>
/// Tests for <see cref="PlaybackExecutionPlanBuilder"/>: verifies the happy-path projection copies
/// every field verbatim, and that every refusal case (PR114a: a pure projection never guesses) is
/// actually refused rather than silently producing an incomplete plan.
/// </summary>
public static class PlaybackExecutionPlanBuilderTests
{
    private static readonly SelectedStreams Streams = new(
        Video: 0,
        Audio: 1,
        Subtitle: new SelectedSubtitle(2, SubtitleDeliveryMethod.External));

    private static readonly OutputSpec Output = new(
        Container: "mp4",
        VideoCodec: "h264",
        AudioCodec: "aac",
        Resolution: new Resolution(1920, 1080),
        VideoRange: "SDR",
        AudioChannels: 2,
        TotalBitrate: 8_000_000,
        VideoBitrate: 6_000_000,
        AudioBitrate: 2_000_000,
        Protocol: StreamingProtocol.Hls,
        SubtitleFormat: "srt");

    private static ReasonNode SampleReasoning() =>
        ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method());

    private static ReasonNode NotViableReasoning() => new(
        ReasonCode.NoViablePlan,
        ReasonOutcome.Rejected,
        ReasonSubject.Method(),
        null,
        []);

    [Fact]
    public static void Build_ViableTranscode_CopiesEveryFieldVerbatim()
    {
        var decision = PlaybackDecision.Transcode(
            "source-1",
            Streams,
            Output,
            [TransformKind.TranscodeVideo, TransformKind.TranscodeAudio],
            SampleReasoning(),
            engineVersion: 6);

        var plan = PlaybackExecutionPlanBuilder.Build(decision);

        Assert.Equal(PlaybackMethod.Transcode, plan.Method);
        Assert.Equal("source-1", plan.SourceId);
        Assert.Equal("mp4", plan.Container);
        Assert.Equal(StreamingProtocol.Hls, plan.Protocol);
        Assert.Equal(0, plan.VideoStreamIndex);
        Assert.Equal("h264", plan.VideoCodec);
        Assert.Equal(6_000_000, plan.VideoBitrate);
        Assert.Equal(new Resolution(1920, 1080), plan.Resolution);
        Assert.Equal("SDR", plan.VideoRange);
        Assert.Equal(1, plan.AudioStreamIndex);
        Assert.Equal("aac", plan.AudioCodec);
        Assert.Equal(2_000_000, plan.AudioBitrate);
        Assert.Equal(2, plan.AudioChannels);
        Assert.Equal(8_000_000, plan.TotalBitrate);
        Assert.Equal(2, plan.SubtitleStreamIndex);
        Assert.Equal(SubtitleDeliveryMethod.External, plan.SubtitleDelivery);
        Assert.Equal("srt", plan.SubtitleFormat);
        Assert.Equal([TransformKind.TranscodeVideo, TransformKind.TranscodeAudio], plan.Transforms);
    }

    [Fact]
    public static void Build_ViableDirectPlayWithNoSubtitle_LeavesSubtitleFieldsNull()
    {
        var streams = new SelectedStreams(Video: 0, Audio: 1, Subtitle: null);
        var output = Output with { SubtitleFormat = null };
        var decision = PlaybackDecision.DirectPlay("source-1", streams, output, SampleReasoning(), engineVersion: 6);

        var plan = PlaybackExecutionPlanBuilder.Build(decision);

        Assert.Null(plan.SubtitleStreamIndex);
        Assert.Null(plan.SubtitleDelivery);
        Assert.Null(plan.SubtitleFormat);
        Assert.Empty(plan.Transforms);
    }

    [Fact]
    public static void TryBuild_NotViableDecision_RefusesWithNotViableReason()
    {
        var decision = PlaybackDecision.NotViable(PlaybackMethod.Transcode, NotViableReasoning(), engineVersion: 6);

        var built = PlaybackExecutionPlanBuilder.TryBuild(decision, out var plan, out var reason);

        Assert.False(built);
        Assert.Null(plan);
        Assert.Equal(PlaybackExecutionPlanRefusalReason.NotViable, reason);
    }

    [Fact]
    public static void TryBuild_NoStreamsSelected_RefusesWithNoStreamsSelectedReason()
    {
        // A hand-built decision that is IsViable but selects nothing: PlaybackDecision's own
        // invariants don't forbid this shape (the real engine simply never produces it, per
        // PlaybackEngine.BuildForSource's own NotViable-on-nothing-to-select branch), so the builder
        // must defend against it independently rather than assume the engine's own guarantee holds
        // for every caller.
        var decision = PlaybackDecision.DirectPlay("source-1", SelectedStreams.None, Output, SampleReasoning(), engineVersion: 6);

        var built = PlaybackExecutionPlanBuilder.TryBuild(decision, out var plan, out var reason);

        Assert.False(built);
        Assert.Null(plan);
        Assert.Equal(PlaybackExecutionPlanRefusalReason.NoStreamsSelected, reason);
    }

    [Fact]
    public static void TryBuild_MissingOutputContainer_RefusesWithMissingOutputContainerReason()
    {
        var output = Output with { Container = null };
        var decision = PlaybackDecision.DirectPlay("source-1", Streams, output, SampleReasoning(), engineVersion: 6);

        var built = PlaybackExecutionPlanBuilder.TryBuild(decision, out var plan, out var reason);

        Assert.False(built);
        Assert.Null(plan);
        Assert.Equal(PlaybackExecutionPlanRefusalReason.MissingOutputContainer, reason);
    }

    [Fact]
    public static void Build_RefusedDecision_ThrowsWithMatchingReason()
    {
        var decision = PlaybackDecision.NotViable(PlaybackMethod.Transcode, NotViableReasoning(), engineVersion: 6);

        var ex = Assert.Throws<PlaybackExecutionPlanRefusedException>(() => PlaybackExecutionPlanBuilder.Build(decision));

        Assert.Equal(PlaybackExecutionPlanRefusalReason.NotViable, ex.Reason);
    }

    [Fact]
    public static void TryBuild_NullDecision_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PlaybackExecutionPlanBuilder.TryBuild(null!, out _, out _));
    }
}
