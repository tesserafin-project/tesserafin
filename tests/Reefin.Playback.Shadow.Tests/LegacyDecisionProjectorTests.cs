using System.Collections.Generic;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Model.Session;
using Xunit;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// Unit tests for <see cref="LegacyDecisionProjector"/>: hand-built <see cref="PlaybackPlan"/>
/// values, checked against the expected <see cref="DecisionVector"/>. Covers reason-category
/// folding and the best-effort transform derivation described on the type.
/// </summary>
public sealed class LegacyDecisionProjectorTests
{
    [Fact]
    public void Project_NullPlan_ReturnsNotViableEmptyVector()
    {
        var vector = LegacyDecisionProjector.Project(null);

        Assert.False(vector.IsViable);
        Assert.Null(vector.Method);
        Assert.True(vector.VideoStreamIndex.IsUnknown);
        Assert.True(vector.AudioStreamIndex.IsUnknown);
        Assert.True(vector.SubtitleStreamIndex.IsUnknown);
        Assert.Empty(vector.TransformClasses);
        Assert.Empty(vector.ReasonCategories);
        Assert.Null(vector.OutputContainer);
        Assert.Null(vector.OutputVideoCodec);
        Assert.Null(vector.OutputAudioCodec);
        Assert.Null(vector.SelectedSource);
        Assert.Null(vector.OutputWidth);
        Assert.Null(vector.OutputHeight);
        Assert.Null(vector.OutputBitrate);
        Assert.Null(vector.OutputVideoRange);
        Assert.Null(vector.OutputAudioChannels);
        Assert.Null(vector.SubtitleDeliveryMode);
    }

    [Fact]
    public void Project_DirectPlay_MapsMethodAndOutputWithNoTransforms()
    {
        var plan = new PlaybackPlan(PlayMethod.DirectPlay, 0, BuildStreamInfo(PlayMethod.DirectPlay, 0, "mp4", audioIndex: 1));

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.True(vector.IsViable);
        Assert.Equal(NormalizedMethod.DirectPlay, vector.Method);
        Assert.Empty(vector.TransformClasses);
        Assert.Empty(vector.ReasonCategories);
        Assert.Equal("mp4", vector.OutputContainer);
        Assert.Equal("h264", vector.OutputVideoCodec);
        Assert.Equal("aac", vector.OutputAudioCodec);

        // Legacy StreamInfo does not expose a video stream index cleanly (per PR98 spec); always Unknown.
        Assert.True(vector.VideoStreamIndex.IsUnknown);
        Assert.True(vector.AudioStreamIndex.IsSelected);
        Assert.Equal(1, vector.AudioStreamIndex.Index);

        // No subtitle index was passed to BuildStreamInfo: legacy positively selected none.
        Assert.True(vector.SubtitleStreamIndex.IsNone);
        Assert.Equal("source-1", vector.SelectedSource);
    }

    [Fact]
    public void Project_DirectStream_NormalizesToRemuxMethod()
    {
        // Legacy PlayMethod.DirectStream is NOT the same enum member as v2 PlaybackMethod.Remux, but
        // both normalize to NormalizedMethod.Remux - this is not a divergence (see ShadowComparer XML docs).
        var plan = new PlaybackPlan(PlayMethod.DirectStream, TranscodeReason.ContainerNotSupported, BuildStreamInfo(PlayMethod.DirectStream, TranscodeReason.ContainerNotSupported, "mp4"));

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.Equal(NormalizedMethod.Remux, vector.Method);
        Assert.Equal(new HashSet<TransformClass> { TransformClass.Remux }, vector.TransformClasses);
        Assert.Equal(new HashSet<ReasonCategory> { ReasonCategory.Container }, vector.ReasonCategories);
    }

    [Theory]
    [InlineData(TranscodeReason.AudioCodecNotSupported, ReasonCategory.AudioCodec)]
    [InlineData(TranscodeReason.AudioChannelsNotSupported, ReasonCategory.AudioChannels)]
    [InlineData(TranscodeReason.AudioSampleRateNotSupported, ReasonCategory.AudioRate)]
    [InlineData(TranscodeReason.VideoCodecNotSupported, ReasonCategory.VideoCodec)]
    [InlineData(TranscodeReason.VideoProfileNotSupported, ReasonCategory.VideoCodec)]
    [InlineData(TranscodeReason.RefFramesNotSupported, ReasonCategory.VideoCodec)]
    [InlineData(TranscodeReason.AnamorphicVideoNotSupported, ReasonCategory.VideoCodec)]
    [InlineData(TranscodeReason.InterlacedVideoNotSupported, ReasonCategory.VideoCodec)]
    [InlineData(TranscodeReason.VideoRangeTypeNotSupported, ReasonCategory.VideoRange)]
    [InlineData(TranscodeReason.VideoResolutionNotSupported, ReasonCategory.VideoDims)]
    [InlineData(TranscodeReason.VideoBitrateNotSupported, ReasonCategory.Bitrate)]
    [InlineData(TranscodeReason.ContainerBitrateExceedsLimit, ReasonCategory.Bitrate)]
    [InlineData(TranscodeReason.SubtitleCodecNotSupported, ReasonCategory.Subtitle)]
    [InlineData(TranscodeReason.SecondaryAudioNotSupported, ReasonCategory.StreamCount)]
    [InlineData(TranscodeReason.AudioIsExternal, ReasonCategory.StreamCount)]
    [InlineData(TranscodeReason.DirectPlayError, ReasonCategory.Error)]
    public void Project_Transcode_FoldsEachReasonBitToItsCategory(TranscodeReason reason, ReasonCategory expectedCategory)
    {
        var plan = new PlaybackPlan(PlayMethod.Transcode, reason, BuildStreamInfo(PlayMethod.Transcode, reason, "mp4"));

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.Contains(expectedCategory, vector.ReasonCategories);
    }

    [Fact]
    public void Project_Transcode_AudioCodecReason_DerivesTranscodeAudioOnly()
    {
        var plan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "mp4"));

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.Equal(new HashSet<TransformClass> { TransformClass.TranscodeAudio }, vector.TransformClasses);
    }

    [Fact]
    public void Project_Transcode_AudioChannelsReason_DerivesTranscodeAudioAndDownmix()
    {
        var plan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioChannelsNotSupported, BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.AudioChannelsNotSupported, "mp4"));

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.Equal(new HashSet<TransformClass> { TransformClass.TranscodeAudio, TransformClass.Downmix }, vector.TransformClasses);
    }

    [Fact]
    public void Project_Transcode_VideoRangeReason_DerivesTranscodeVideoAndTonemap()
    {
        var plan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported, BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported, "mp4"));

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.Equal(new HashSet<TransformClass> { TransformClass.TranscodeVideo, TransformClass.Tonemap }, vector.TransformClasses);
    }

    [Fact]
    public void Project_Transcode_SubtitleReason_DerivesBurnInSubtitle()
    {
        var plan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.SubtitleCodecNotSupported, BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.SubtitleCodecNotSupported, "mp4"));

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.Equal(new HashSet<TransformClass> { TransformClass.BurnInSubtitle }, vector.TransformClasses);
    }

    [Fact]
    public void Project_Transcode_ContainerReason_DerivesRemuxAlongsideOtherTransforms()
    {
        var reasons = TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported;
        var plan = new PlaybackPlan(PlayMethod.Transcode, reasons, BuildStreamInfo(PlayMethod.Transcode, reasons, "mp4"));

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.Equal(new HashSet<TransformClass> { TransformClass.Remux, TransformClass.TranscodeAudio }, vector.TransformClasses);
    }

    [Fact]
    public void Project_NoStreamInfo_LeavesIndicesAndOutputNull()
    {
        var plan = new PlaybackPlan(PlayMethod.DirectPlay, 0, StreamInfo: null);

        var vector = LegacyDecisionProjector.Project(plan);

        Assert.True(vector.IsViable);
        Assert.True(vector.AudioStreamIndex.IsUnknown);

        // With no StreamInfo at all, the projector has no basis to say "no subtitle": Unknown, not None.
        Assert.True(vector.SubtitleStreamIndex.IsUnknown);
        Assert.Null(vector.OutputContainer);
        Assert.Null(vector.OutputVideoCodec);
        Assert.Null(vector.OutputAudioCodec);
        Assert.Null(vector.SelectedSource);
        Assert.Null(vector.OutputVideoRange);
        Assert.Null(vector.SubtitleDeliveryMode);
    }

    [Fact]
    public void Project_HdrSourceDirectPlayed_MapsVideoRangeToHdr10()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.DirectPlay, 0, "mp4", colorTransfer: "smpte2084");

        var vector = LegacyDecisionProjector.Project(new PlaybackPlan(PlayMethod.DirectPlay, 0, streamInfo));

        Assert.Equal("HDR10", vector.OutputVideoRange);
    }

    [Fact]
    public void Project_SdrSourceDirectPlayed_MapsVideoRangeToSdr()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.DirectPlay, 0, "mp4");

        var vector = LegacyDecisionProjector.Project(new PlaybackPlan(PlayMethod.DirectPlay, 0, streamInfo));

        Assert.Equal("SDR", vector.OutputVideoRange);
    }

    [Fact]
    public void Project_SubtitleSelectedWithEncodeDeliveryMethod_MapsToBurnAndSelectedIndex()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.SubtitleCodecNotSupported, "mp4", subtitleIndex: 2, subtitleDeliveryMethod: SubtitleDeliveryMethod.Encode);

        var vector = LegacyDecisionProjector.Project(new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.SubtitleCodecNotSupported, streamInfo));

        Assert.True(vector.SubtitleStreamIndex.IsSelected);
        Assert.Equal(2, vector.SubtitleStreamIndex.Index);
        Assert.Equal(SubtitleDeliveryMode.Burn, vector.SubtitleDeliveryMode);
    }

    private static StreamInfo BuildStreamInfo(
        PlayMethod playMethod,
        TranscodeReason reasons,
        string container,
        int? audioIndex = null,
        int? subtitleIndex = null,
        SubtitleDeliveryMethod subtitleDeliveryMethod = SubtitleDeliveryMethod.Embed,
        string? colorTransfer = null)
    {
        var mediaSource = new MediaSourceInfo
        {
            Id = "source-1",
            Container = container,
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", ColorTransfer = colorTransfer },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", IsDefault = true },
            ],
        };

        return new StreamInfo
        {
            DeviceProfile = new DeviceProfile(),
            PlayMethod = playMethod,
            TranscodeReasons = reasons,
            Container = container,
            AudioStreamIndex = audioIndex,
            SubtitleStreamIndex = subtitleIndex,
            SubtitleDeliveryMethod = subtitleDeliveryMethod,
            MediaSource = mediaSource,
        };
    }
}
