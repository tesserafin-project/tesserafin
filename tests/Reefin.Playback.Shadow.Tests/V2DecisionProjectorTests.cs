using System.Collections.Generic;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// Unit tests for <see cref="V2DecisionProjector"/>: hand-built <see cref="PlaybackDecision"/>
/// values (via the domain's validating factories), checked against the expected
/// <see cref="DecisionVector"/>.
/// </summary>
public sealed class V2DecisionProjectorTests
{
    [Fact]
    public void Project_NotViable_ReturnsNotViableVector()
    {
        var reasoning = new ReasonNode(
            ReasonCode.NoViablePlan,
            ReasonOutcome.Rejected,
            ReasonSubject.Method(),
            null,
            [ReasonNode.Leaf(ReasonCode.VideoCodecNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(0))]);
        var decision = PlaybackDecision.NotViable(PlaybackMethod.Transcode, reasoning, engineVersion: 2);

        var vector = V2DecisionProjector.Project(decision);

        Assert.False(vector.IsViable);
        Assert.Null(vector.Method);
        Assert.Empty(vector.TransformClasses);

        // VideoCodecNotSupported is a real reason category, unrelated to the NoViablePlan marker
        // itself (which carries no category): it should still be folded in.
        Assert.Contains(ReasonCategory.VideoCodec, vector.ReasonCategories);

        // PlaybackDecision.NotViable's SelectedStreams.None/OutputSpec.Empty are placeholder
        // defaults, not real facts: the projector must not turn them into "no stream selected".
        Assert.True(vector.VideoStreamIndex.IsUnknown);
        Assert.True(vector.AudioStreamIndex.IsUnknown);
        Assert.True(vector.SubtitleStreamIndex.IsUnknown);
        Assert.Null(vector.SelectedSource);
        Assert.Null(vector.OutputVideoRange);
        Assert.Null(vector.SubtitleDeliveryMode);
    }

    [Fact]
    public void Project_DirectPlay_MapsMethodStreamsAndOutput()
    {
        var streams = new SelectedStreams(0, 1, null);
        var output = new OutputSpec("mp4", "h264", "aac", null, null, null, null);
        var reasoning = ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method());
        var decision = PlaybackDecision.DirectPlay("source-1", streams, output, reasoning, engineVersion: 2);

        var vector = V2DecisionProjector.Project(decision);

        Assert.True(vector.IsViable);
        Assert.Equal(NormalizedMethod.DirectPlay, vector.Method);
        Assert.True(vector.VideoStreamIndex.IsSelected);
        Assert.Equal(0, vector.VideoStreamIndex.Index);
        Assert.True(vector.AudioStreamIndex.IsSelected);
        Assert.Equal(1, vector.AudioStreamIndex.Index);

        // No SelectedSubtitle on the decision: v2 knows definitively that none was selected.
        Assert.True(vector.SubtitleStreamIndex.IsNone);
        Assert.Equal(SubtitleDeliveryMode.None, vector.SubtitleDeliveryMode);
        Assert.Empty(vector.TransformClasses);
        Assert.Equal("mp4", vector.OutputContainer);
        Assert.Equal("h264", vector.OutputVideoCodec);
        Assert.Equal("aac", vector.OutputAudioCodec);
        Assert.Equal("source-1", vector.SelectedSource);

        // MethodChosen is a positive marker code, not in the reason-category map.
        Assert.Empty(vector.ReasonCategories);
    }

    [Fact]
    public void Project_Remux_MapsRemuxContainerToRemuxTransformClass()
    {
        var streams = new SelectedStreams(0, 1, null);
        var output = new OutputSpec("mp4", "h264", "aac", null, null, null, null);
        var transforms = new List<TransformKind> { TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.CopyAudio };
        var reasoning = ReasonNode.Leaf(ReasonCode.ContainerNotSupported, ReasonOutcome.Rejected, ReasonSubject.Container());
        var decision = PlaybackDecision.Remux("source-1", streams, output, transforms, reasoning, engineVersion: 2);

        var vector = V2DecisionProjector.Project(decision);

        Assert.Equal(NormalizedMethod.Remux, vector.Method);

        // CopyVideo/CopyAudio carry no comparable legacy signal and are omitted by design.
        Assert.Equal(new HashSet<TransformClass> { TransformClass.Remux }, vector.TransformClasses);
        Assert.Equal(new HashSet<ReasonCategory> { ReasonCategory.Container }, vector.ReasonCategories);
    }

    [Fact]
    public void Project_Transcode_MapsAllTransformKindsAndFoldsReasonTree()
    {
        var streams = new SelectedStreams(0, 1, new SelectedSubtitle(2, SubtitleDeliveryMethod.Burn));
        var output = new OutputSpec("mp4", "h264", "aac", null, "SDR", 2, null);
        var transforms = new List<TransformKind>
        {
            TransformKind.TranscodeVideo,
            TransformKind.Tonemap,
            TransformKind.TranscodeAudio,
            TransformKind.Downmix,
            TransformKind.BurnInSubtitle,
            TransformKind.ExtractSubtitle,
        };

        var reasoning = new ReasonNode(
            ReasonCode.MethodChosen,
            ReasonOutcome.Chosen,
            ReasonSubject.Method(),
            null,
            [
                ReasonNode.Leaf(ReasonCode.VideoRangeTypeNotSupported, ReasonOutcome.Rejected, ReasonSubject.VideoStream(0)),
                ReasonNode.Leaf(ReasonCode.TonemapRequired, ReasonOutcome.Chosen, ReasonSubject.VideoStream(0)),
                ReasonNode.Leaf(ReasonCode.AudioChannelsNotSupported, ReasonOutcome.Rejected, ReasonSubject.AudioStream(1)),
                ReasonNode.Leaf(ReasonCode.DownmixRequired, ReasonOutcome.Chosen, ReasonSubject.AudioStream(1)),
                ReasonNode.Leaf(ReasonCode.SubtitleBurnInRequired, ReasonOutcome.Chosen, ReasonSubject.Subtitle(2)),
            ]);

        var decision = PlaybackDecision.Transcode("source-1", streams, output, transforms, reasoning, engineVersion: 2);

        var vector = V2DecisionProjector.Project(decision);

        Assert.Equal(NormalizedMethod.Transcode, vector.Method);
        Assert.Equal(
            new HashSet<TransformClass>
            {
                TransformClass.TranscodeVideo,
                TransformClass.Tonemap,
                TransformClass.TranscodeAudio,
                TransformClass.Downmix,
                TransformClass.BurnInSubtitle,
                TransformClass.ExtractSubtitle,
            },
            vector.TransformClasses);

        // TonemapRequired/DownmixRequired/SubtitleBurnInRequired/MethodChosen are positive/marker
        // codes and are excluded; only the real constraint codes fold into categories.
        Assert.Equal(new HashSet<ReasonCategory> { ReasonCategory.VideoRange, ReasonCategory.AudioChannels }, vector.ReasonCategories);

        Assert.True(vector.SubtitleStreamIndex.IsSelected);
        Assert.Equal(2, vector.SubtitleStreamIndex.Index);
        Assert.Equal(SubtitleDeliveryMode.Burn, vector.SubtitleDeliveryMode);
        Assert.Equal("SDR", vector.OutputVideoRange);
        Assert.Equal(2, vector.OutputAudioChannels);
    }
}
