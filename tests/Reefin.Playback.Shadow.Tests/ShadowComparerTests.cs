using System.Collections.Generic;
using Xunit;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// Classification tests for <see cref="ShadowComparer"/>: hand-built <see cref="DecisionVector"/>
/// pairs constructed to land in each <see cref="DivergenceClass"/>.
/// </summary>
public sealed class ShadowComparerTests
{
    [Fact]
    public void Compare_IdenticalVectors_ClassifiesEquivalent()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.Equivalent, divergence.Class);
        Assert.False(divergence.MethodDiffers);
        Assert.False(divergence.StreamsDiffer);
    }

    [Fact]
    public void Compare_NullIndexOnEitherSide_DoesNotCountAsStreamDivergence()
    {
        // Legacy never reports a video stream index (PR98 spec); a v2 index alongside a legacy null
        // must not, by itself, break equivalence.
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.False(divergence.StreamsDiffer);
        Assert.Equal(DivergenceClass.Equivalent, divergence.Class);
    }

    [Fact]
    public void Compare_TwoDifferingNonNullIndices_CountsAsStreamDivergence()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: null, audio: 2, container: "mp4", videoCodec: "h264", audioCodec: "aac");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.True(divergence.StreamsDiffer);
        Assert.NotEqual(DivergenceClass.Equivalent, divergence.Class);
    }

    [Fact]
    public void Compare_LegacyNotViableV2Viable_ClassifiesExpectedImprovement()
    {
        var legacy = NotViableVector();
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.ExpectedImprovement, divergence.Class);
    }

    [Fact]
    public void Compare_V2DirectPlayWhereLegacyTranscode_ClassifiesExpectedImprovement()
    {
        var legacy = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio]);
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.ExpectedImprovement, divergence.Class);
    }

    [Fact]
    public void Compare_SameMethodV2TransformsProperSubset_ClassifiesExpectedImprovement()
    {
        var legacy = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio, TransformClass.Downmix]);
        var v2 = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio]);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.ExpectedImprovement, divergence.Class);
    }

    [Fact]
    public void Compare_LegacyViableV2NotViable_ClassifiesPotentialRegression()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");
        var v2 = NotViableVector();

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.PotentialRegression, divergence.Class);
    }

    [Fact]
    public void Compare_V2TranscodeWhereLegacyDirectPlay_ClassifiesPotentialRegression()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");
        var v2 = Vector(true, NormalizedMethod.Transcode, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio]);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.PotentialRegression, divergence.Class);
    }

    [Fact]
    public void Compare_SameMethodV2TransformsProperSuperset_ClassifiesPotentialRegression()
    {
        var legacy = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio]);
        var v2 = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio, TransformClass.Downmix]);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.PotentialRegression, divergence.Class);
    }

    [Fact]
    public void Compare_SameMethodTransformsDifferInBothDirections_ClassifiesUnexplained()
    {
        var legacy = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeVideo]);
        var v2 = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio]);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.Unexplained, divergence.Class);
        Assert.Contains(TransformClass.TranscodeVideo, divergence.OnlyLegacy);
        Assert.Contains(TransformClass.TranscodeAudio, divergence.OnlyV2);
    }

    [Fact]
    public void Compare_SameMethodReasonsDifferInBothDirections_ClassifiesUnexplained()
    {
        var legacy = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio], reasons: [ReasonCategory.AudioCodec]);
        var v2 = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio], reasons: [ReasonCategory.AudioRate]);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.Unexplained, divergence.Class);
    }

    [Fact]
    public void Compare_SameMethodSameWorkDifferentOutputCodec_ClassifiesKnownV2Limitation()
    {
        var legacy = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "mp3", transforms: [TransformClass.TranscodeAudio], reasons: [ReasonCategory.AudioCodec]);
        var v2 = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeAudio], reasons: [ReasonCategory.AudioCodec]);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.KnownV2Limitation, divergence.Class);
    }

    [Fact]
    public void Compare_ContainerCodecComparison_IsCaseInsensitive()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "MP4", videoCodec: "H264", audioCodec: "AAC");
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.Equivalent, divergence.Class);
    }

    [Fact]
    public void Compare_SubtitleNoneOnOneSideSelectedOnOther_ClassifiesPotentialRegression()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", subtitleNone: true);
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", subtitle: 4);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.True(divergence.StreamsDiffer);
        Assert.Equal(DivergenceClass.PotentialRegression, divergence.Class);
    }

    [Fact]
    public void Compare_DifferentSelectedSources_ClassifiesPotentialRegression()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", selectedSource: "source-1");
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", selectedSource: "source-2");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.PotentialRegression, divergence.Class);
    }

    [Fact]
    public void Compare_SameMethodSameTransformsDifferentVideoRange_ClassifiesPotentialRegression()
    {
        var legacy = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeVideo], outputVideoRange: "HDR10");
        var v2 = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeVideo], outputVideoRange: "SDR");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.PotentialRegression, divergence.Class);
    }

    [Fact]
    public void Compare_SameMethodSameTransformsDifferentResolution_ClassifiesPotentialRegression()
    {
        var legacy = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeVideo], outputWidth: 1920, outputHeight: 1080);
        var v2 = Vector(true, NormalizedMethod.Transcode, video: null, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", transforms: [TransformClass.TranscodeVideo], outputWidth: 1280, outputHeight: 720);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.PotentialRegression, divergence.Class);
    }

    [Fact]
    public void Compare_UnknownFieldOnOneSide_DoesNotCountAsDivergenceForThatField()
    {
        // legacy never determined the video range (Unknown/null); v2 reports HDR10. Everything else
        // matches, so the vectors must still be classified Equivalent - an unknown value on one side
        // must not manufacture a divergence.
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", outputVideoRange: null);
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", outputVideoRange: "HDR10");

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.Equal(DivergenceClass.Equivalent, divergence.Class);
    }

    [Fact]
    public void Compare_SubtitleNoneOnBothSides_IsEquivalent()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", subtitleNone: true);
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", subtitleNone: true);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.False(divergence.StreamsDiffer);
        Assert.Equal(DivergenceClass.Equivalent, divergence.Class);
    }

    [Fact]
    public void Compare_SubtitleSelectedSameIndexOnBothSides_IsEquivalent()
    {
        var legacy = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", subtitle: 2);
        var v2 = Vector(true, NormalizedMethod.DirectPlay, video: 0, audio: 1, container: "mp4", videoCodec: "h264", audioCodec: "aac", subtitle: 2);

        var divergence = ShadowComparer.Compare(legacy, v2);

        Assert.False(divergence.StreamsDiffer);
        Assert.Equal(DivergenceClass.Equivalent, divergence.Class);
    }

    private static DecisionVector Vector(
        bool isViable,
        NormalizedMethod? method,
        int? video,
        int? audio,
        string? container,
        string? videoCodec,
        string? audioCodec,
        IEnumerable<TransformClass>? transforms = null,
        IEnumerable<ReasonCategory>? reasons = null,
        int? subtitle = null,
        bool subtitleNone = false,
        string? selectedSource = null,
        int? outputWidth = null,
        int? outputHeight = null,
        int? outputBitrate = null,
        string? outputVideoRange = null,
        int? outputAudioChannels = null,
        SubtitleDeliveryMode? subtitleDeliveryMode = null) =>
        new(
            isViable,
            method,
            ToSelection(video),
            ToSelection(audio),
            subtitleNone ? StreamSelection.None : ToSelection(subtitle),
            TransformClasses: transforms is null ? new HashSet<TransformClass>() : new HashSet<TransformClass>(transforms),
            ReasonCategories: reasons is null ? new HashSet<ReasonCategory>() : new HashSet<ReasonCategory>(reasons),
            OutputContainer: container,
            OutputVideoCodec: videoCodec,
            OutputAudioCodec: audioCodec,
            SelectedSource: selectedSource,
            OutputWidth: outputWidth,
            OutputHeight: outputHeight,
            OutputBitrate: outputBitrate,
            OutputVideoRange: outputVideoRange,
            OutputAudioChannels: outputAudioChannels,
            SubtitleDeliveryMode: subtitleDeliveryMode);

    /// <summary>
    /// Mirrors the pre-PR101 int?-based test convention: a <see langword="null"/> index means "not
    /// asserted" (Unknown), never "explicitly none". Tests that need <see cref="StreamSelection.None"/>
    /// use <c>subtitleNone: true</c> instead.
    /// </summary>
    private static StreamSelection ToSelection(int? index) => index is int i ? StreamSelection.Selected(i) : StreamSelection.Unknown;

    private static DecisionVector NotViableVector() =>
        new(
            false,
            null,
            StreamSelection.Unknown,
            StreamSelection.Unknown,
            StreamSelection.Unknown,
            new HashSet<TransformClass>(),
            new HashSet<ReasonCategory>(),
            null,
            null,
            null,
            SelectedSource: null,
            OutputWidth: null,
            OutputHeight: null,
            OutputBitrate: null,
            OutputVideoRange: null,
            OutputAudioChannels: null,
            SubtitleDeliveryMode: null);
}
