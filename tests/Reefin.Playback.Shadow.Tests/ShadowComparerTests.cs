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

    private static DecisionVector Vector(
        bool isViable,
        NormalizedMethod? method,
        int? video,
        int? audio,
        string? container,
        string? videoCodec,
        string? audioCodec,
        IEnumerable<TransformClass>? transforms = null,
        IEnumerable<ReasonCategory>? reasons = null) =>
        new(
            isViable,
            method,
            video,
            audio,
            SubtitleStreamIndex: null,
            TransformClasses: transforms is null ? new HashSet<TransformClass>() : new HashSet<TransformClass>(transforms),
            ReasonCategories: reasons is null ? new HashSet<ReasonCategory>() : new HashSet<ReasonCategory>(reasons),
            OutputContainer: container,
            OutputVideoCodec: videoCodec,
            OutputAudioCodec: audioCodec);

    private static DecisionVector NotViableVector() =>
        new(
            false,
            null,
            null,
            null,
            null,
            new HashSet<TransformClass>(),
            new HashSet<ReasonCategory>(),
            null,
            null,
            null);
}
