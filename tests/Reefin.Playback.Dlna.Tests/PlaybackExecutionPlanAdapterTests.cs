using System;
using Reefin.Data.Enums;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Session;
using Reefin.Playback.Decision;
using Reefin.Playback.Execution;
using Xunit;
using LegacySubtitleDeliveryMethod = Reefin.Model.Dlna.SubtitleDeliveryMethod;

namespace Reefin.Playback.Dlna.Tests;

/// <summary>
/// Tests for <see cref="PlaybackExecutionPlanAdapter"/>: verifies the fields it fills are copied
/// verbatim from the plan (never re-decided), and that a mismatched source id is rejected rather than
/// silently substituted.
/// </summary>
public static class PlaybackExecutionPlanAdapterTests
{
    private static MediaSourceInfo BuildMediaSource(string id = "source-1") => new()
    {
        Id = id,
        Container = "mkv",
    };

    private static PlaybackExecutionPlan BuildPlan(
        PlaybackMethod method = PlaybackMethod.Transcode,
        string sourceId = "source-1",
        int? videoIndex = 0,
        int? audioIndex = 1,
        int? subtitleIndex = 2,
        Reefin.Playback.Decision.SubtitleDeliveryMethod? subtitleDelivery = Reefin.Playback.Decision.SubtitleDeliveryMethod.External,
        string? subtitleFormat = "srt") => new(
        Method: method,
        SourceId: sourceId,
        Container: "mp4",
        Protocol: StreamingProtocol.Hls,
        VideoStreamIndex: videoIndex,
        VideoCodec: videoIndex is null ? null : "h264",
        VideoBitrate: videoIndex is null ? null : 6_000_000,
        Resolution: videoIndex is null ? null : new Resolution(1920, 1080),
        VideoRange: videoIndex is null ? null : "SDR",
        AudioStreamIndex: audioIndex,
        AudioCodec: audioIndex is null ? null : "aac",
        AudioBitrate: audioIndex is null ? null : 2_000_000,
        AudioChannels: audioIndex is null ? null : 2,
        TotalBitrate: 8_000_000,
        SubtitleStreamIndex: subtitleIndex,
        SubtitleDelivery: subtitleIndex is null ? null : subtitleDelivery,
        SubtitleFormat: subtitleIndex is null ? null : subtitleFormat,
        Transforms: [TransformKind.TranscodeVideo, TransformKind.TranscodeAudio]);

    [Theory]
    [InlineData(PlaybackMethod.DirectPlay, PlayMethod.DirectPlay)]
    [InlineData(PlaybackMethod.Remux, PlayMethod.DirectStream)]
    [InlineData(PlaybackMethod.Transcode, PlayMethod.Transcode)]
    public static void ToStreamInfo_MapsMethodExactly(PlaybackMethod method, PlayMethod expected)
    {
        var plan = BuildPlan(method: method);
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, mediaSource, deviceProfile, Guid.NewGuid());

        Assert.Equal(expected, streamInfo.PlayMethod);
    }

    [Fact]
    public static void ToStreamInfo_CopiesSourceStreamsAndTargetsVerbatim()
    {
        var plan = BuildPlan();
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var itemId = Guid.NewGuid();

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, mediaSource, deviceProfile, itemId, deviceId: "device-1", playSessionId: "play-1");

        Assert.Equal(itemId, streamInfo.ItemId);
        Assert.Same(mediaSource, streamInfo.MediaSource);
        Assert.Equal("source-1", streamInfo.MediaSourceId);
        Assert.Equal("mp4", streamInfo.Container);
        Assert.Equal(MediaStreamProtocol.hls, streamInfo.SubProtocol);
        Assert.Equal(DlnaProfileType.Video, streamInfo.MediaType);
        Assert.Equal(1, streamInfo.AudioStreamIndex);
        Assert.Equal(["h264"], streamInfo.VideoCodecs);
        Assert.Equal(["aac"], streamInfo.AudioCodecs);
        Assert.Equal(6_000_000, streamInfo.VideoBitrate);
        Assert.Equal(2_000_000, streamInfo.AudioBitrate);
        Assert.Equal(2, streamInfo.GlobalMaxAudioChannels);
        Assert.Equal(1920, streamInfo.MaxWidth);
        Assert.Equal(1080, streamInfo.MaxHeight);
        Assert.Equal(2, streamInfo.SubtitleStreamIndex);
        Assert.Equal(LegacySubtitleDeliveryMethod.External, streamInfo.SubtitleDeliveryMethod);
        Assert.Equal("srt", streamInfo.SubtitleFormat);
        Assert.Same(deviceProfile, streamInfo.DeviceProfile);
        Assert.Equal("device-1", streamInfo.DeviceId);
        Assert.Equal("play-1", streamInfo.PlaySessionId);
    }

    [Fact]
    public static void ToStreamInfo_NoSubtitleSelected_LeavesSubtitleFieldsAtDefault()
    {
        var plan = BuildPlan(subtitleIndex: null);
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, mediaSource, deviceProfile, Guid.NewGuid());

        Assert.Null(streamInfo.SubtitleStreamIndex);
        Assert.Null(streamInfo.SubtitleFormat);
    }

    [Fact]
    public static void ToStreamInfo_NoVideoStreamSelected_InfersAudioMediaType()
    {
        var plan = BuildPlan(videoIndex: null);
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, mediaSource, deviceProfile, Guid.NewGuid());

        Assert.Equal(DlnaProfileType.Audio, streamInfo.MediaType);
        Assert.Empty(streamInfo.VideoCodecs);
    }

    [Fact]
    public static void ToStreamInfo_MismatchedSourceId_Throws()
    {
        var plan = BuildPlan(sourceId: "source-1");
        var mediaSource = BuildMediaSource(id: "source-2");
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();

        Assert.Throws<ArgumentException>(() => PlaybackExecutionPlanAdapter.ToStreamInfo(plan, mediaSource, deviceProfile, Guid.NewGuid()));
    }

    [Fact]
    public static void ToStreamInfo_NullPlan_Throws()
    {
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();

        Assert.Throws<ArgumentNullException>(() => PlaybackExecutionPlanAdapter.ToStreamInfo(null!, mediaSource, deviceProfile, Guid.NewGuid()));
    }
}
