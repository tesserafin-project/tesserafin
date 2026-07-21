using System;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Execution;
using Xunit;
using LegacySubtitleDeliveryMethod = Tesserafin.Model.Dlna.SubtitleDeliveryMethod;

namespace Tesserafin.Playback.Dlna.Tests;

/// <summary>
/// Tests for <see cref="PlaybackExecutionPlanAdapter"/>: verifies the fields it fills are copied
/// verbatim from the plan/context (never re-decided), that source-scoped (§3.B) and device-profile-scoped
/// (§3.C) facts - including <see cref="StreamInfo.RequireAvc"/>/<see cref="StreamInfo.RequireNonAnamorphic"/>,
/// mandatory per PR115b's design doc "Invariant de parité exécutable" - resolve correctly, and that a
/// mismatched source id is rejected rather than silently substituted.
/// </summary>
public static class PlaybackExecutionPlanAdapterTests
{
    private static MediaSourceInfo BuildMediaSource(string id = "source-1", long? runTimeTicks = null, params MediaStream[] streams) => new()
    {
        Id = id,
        Container = "mkv",
        RunTimeTicks = runTimeTicks,
        MediaStreams = streams,
    };

    private static MediaStream BuildVideoStream(int index = 0, string codec = "h264", float? referenceFrameRate = 23.976f, double? level = 41, int? bitDepth = 8, string? profile = "high") => new()
    {
        Index = index,
        Type = MediaStreamType.Video,
        Codec = codec,
        AverageFrameRate = referenceFrameRate,
        Level = level,
        BitDepth = bitDepth,
        Profile = profile,
    };

    private static MediaStream BuildAudioStream(int index = 1, string codec = "aac", int? sampleRate = 48000, int? channels = 2, double? level = null, string? profile = null) => new()
    {
        Index = index,
        Type = MediaStreamType.Audio,
        Codec = codec,
        SampleRate = sampleRate,
        Channels = channels,
        Level = level,
        Profile = profile,
    };

    private static PlaybackExecutionContext BuildContext(Guid itemId, string? deviceId = null, string? deviceProfileId = null, string? playSessionId = null, long startPositionTicks = 0, bool alwaysBurnIn = false) =>
        new(itemId, playSessionId, deviceId, deviceProfileId, startPositionTicks, alwaysBurnIn);

    private static PlaybackExecutionPlan BuildPlan(
        PlaybackMethod method = PlaybackMethod.Transcode,
        string sourceId = "source-1",
        string container = "mp4",
        int? videoIndex = 0,
        int? audioIndex = 1,
        int? subtitleIndex = 2,
        Tesserafin.Playback.Decision.SubtitleDeliveryMethod? subtitleDelivery = Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External,
        string? subtitleFormat = "srt",
        string? videoCodec = "h264",
        string? audioCodec = "aac",
        TransformKind[]? transforms = null) => new(
        Method: method,
        SourceId: sourceId,
        Container: container,
        Protocol: StreamingProtocol.Hls,
        VideoStreamIndex: videoIndex,
        VideoCodec: videoIndex is null ? null : videoCodec,
        VideoBitrate: videoIndex is null ? null : 6_000_000,
        Resolution: videoIndex is null ? null : new Resolution(1920, 1080),
        VideoRange: videoIndex is null ? null : "SDR",
        AudioStreamIndex: audioIndex,
        AudioCodec: audioIndex is null ? null : audioCodec,
        AudioBitrate: audioIndex is null ? null : 2_000_000,
        AudioChannels: audioIndex is null ? null : 2,
        TotalBitrate: 8_000_000,
        SubtitleStreamIndex: subtitleIndex,
        SubtitleDelivery: subtitleIndex is null ? null : subtitleDelivery,
        SubtitleFormat: subtitleIndex is null ? null : subtitleFormat,
        Transforms: transforms ?? [TransformKind.TranscodeVideo]);

    [Theory]
    [InlineData(PlaybackMethod.DirectPlay, PlayMethod.DirectPlay)]
    [InlineData(PlaybackMethod.Remux, PlayMethod.DirectStream)]
    [InlineData(PlaybackMethod.Transcode, PlayMethod.Transcode)]
    public static void ToStreamInfo_MapsMethodExactly(PlaybackMethod method, PlayMethod expected)
    {
        var plan = BuildPlan(method: method);
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Equal(expected, streamInfo.PlayMethod);
    }

    [Fact]
    public static void ToStreamInfo_CopiesSourceStreamsAndTargetsVerbatim()
    {
        var plan = BuildPlan();
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var itemId = Guid.NewGuid();
        var context = BuildContext(itemId, deviceId: "device-1", playSessionId: "play-1");

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

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
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Null(streamInfo.SubtitleStreamIndex);
        Assert.Null(streamInfo.SubtitleFormat);
    }

    [Fact]
    public static void ToStreamInfo_NoVideoStreamSelected_InfersAudioMediaType()
    {
        var plan = BuildPlan(videoIndex: null);
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Equal(DlnaProfileType.Audio, streamInfo.MediaType);
        Assert.Empty(streamInfo.VideoCodecs);
    }

    [Fact]
    public static void ToStreamInfo_MismatchedSourceId_Throws()
    {
        var plan = BuildPlan(sourceId: "source-1");
        var mediaSource = BuildMediaSource(id: "source-2");
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile));
    }

    [Fact]
    public static void ToStreamInfo_NullPlan_Throws()
    {
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        Assert.Throws<ArgumentNullException>(() => PlaybackExecutionPlanAdapter.ToStreamInfo(null!, context, mediaSource, deviceProfile));
    }

    [Fact]
    public static void ToStreamInfo_NullContext_Throws()
    {
        var plan = BuildPlan();
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();

        Assert.Throws<ArgumentNullException>(() => PlaybackExecutionPlanAdapter.ToStreamInfo(plan, null!, mediaSource, deviceProfile));
    }

    // --- §3.A: request-scoped context, carried verbatim -----------------------------------------

    [Fact]
    public static void ToStreamInfo_ContextFields_CarriedVerbatim()
    {
        var plan = BuildPlan();
        var mediaSource = BuildMediaSource();
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var itemId = Guid.NewGuid();
        var context = BuildContext(itemId, deviceId: "device-9", deviceProfileId: "profile-9", playSessionId: "sess-9", startPositionTicks: 123_456, alwaysBurnIn: true);

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Equal(itemId, streamInfo.ItemId);
        Assert.Equal("device-9", streamInfo.DeviceId);
        Assert.Equal("profile-9", streamInfo.DeviceProfileId);
        Assert.Equal("sess-9", streamInfo.PlaySessionId);
        Assert.Equal(123_456, streamInfo.StartPositionTicks);
        Assert.True(streamInfo.AlwaysBurnInSubtitleWhenTranscoding);
    }

    // --- §3.B: source-scoped facts, read off the selected media streams -------------------------

    [Fact]
    public static void ToStreamInfo_Transcode_ResolvesRunTimeTicksAndMaxFramerateFromSource()
    {
        var plan = BuildPlan(method: PlaybackMethod.Transcode, transforms: [TransformKind.TranscodeVideo]);
        var videoStream = BuildVideoStream(referenceFrameRate: 29.97f);
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(runTimeTicks: 100_000_000, streams: [videoStream, audioStream]);
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Equal(100_000_000, streamInfo.RunTimeTicks);
        Assert.Equal(29.97f, streamInfo.MaxFramerate);
    }

    [Fact]
    public static void ToStreamInfo_DirectPlay_DoesNotSetSourceVideoOptions()
    {
        var plan = BuildPlan(method: PlaybackMethod.DirectPlay, transforms: []);
        var videoStream = BuildVideoStream();
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Null(streamInfo.MaxFramerate);
        Assert.Null(streamInfo.GetOption("h264", "level"));
    }

    [Fact]
    public static void ToStreamInfo_Remux_SetsSourceVideoCodecQualifiedOptions()
    {
        var plan = BuildPlan(method: PlaybackMethod.Remux, transforms: []);
        var videoStream = BuildVideoStream(codec: "h264", level: 41, bitDepth: 8, profile: "High");
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Equal("41", streamInfo.GetOption("h264", "level"));
        Assert.Equal("8", streamInfo.GetOption("h264", "videobitdepth"));
        Assert.Equal("high", streamInfo.GetOption("h264", "profile"));
    }

    [Fact]
    public static void ToStreamInfo_AudioCopied_ResolvesAudioSampleRateAndOptions()
    {
        var plan = BuildPlan(method: PlaybackMethod.Transcode, transforms: [TransformKind.TranscodeVideo]);
        var videoStream = BuildVideoStream();
        var audioStream = BuildAudioStream(codec: "aac", sampleRate: 44100, channels: 6, level: 2, profile: "LC");
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Equal(44100, streamInfo.AudioSampleRate);
        // Legacy quirk (StreamBuilder.BuildStreamVideoItem), reproduced verbatim: "audiochannels" is
        // keyed by the VIDEO codec, not the audio codec.
        Assert.Equal("6", streamInfo.GetOption("h264", "audiochannels"));
        Assert.Equal("lc", streamInfo.GetOption("aac", "profile"));
        Assert.Equal("2", streamInfo.GetOption("aac", "level"));
    }

    [Fact]
    public static void ToStreamInfo_AudioTranscoded_DoesNotResolveAudioSampleRate()
    {
        var plan = BuildPlan(method: PlaybackMethod.Transcode, audioCodec: "aac", transforms: [TransformKind.TranscodeVideo, TransformKind.TranscodeAudio]);
        var videoStream = BuildVideoStream();
        var audioStream = BuildAudioStream(codec: "ac3", sampleRate: 48000);
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Null(streamInfo.AudioSampleRate);
    }

    // --- §3.C: device-profile-scoped facts (TranscodingProfile block), Transcode only -----------

    [Fact]
    public static void ToStreamInfo_Transcode_ResolvesTranscodingProfileFields()
    {
        var plan = BuildPlan(method: PlaybackMethod.Transcode, container: "mp4", transforms: [TransformKind.TranscodeVideo]);
        var videoStream = BuildVideoStream();
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        // DeviceProfileFixture's "mp4" TranscodingProfile declares MaxAudioChannels="6" and no
        // other knobs - just enough to prove the profile was actually matched and consulted.
        Assert.Equal(6, streamInfo.TranscodingMaxAudioChannels);
    }

    [Fact]
    public static void ToStreamInfo_DirectStream_DoesNotResolveTranscodingProfileFields()
    {
        var plan = BuildPlan(method: PlaybackMethod.Remux, container: "mp4", transforms: []);
        var videoStream = BuildVideoStream();
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.Null(streamInfo.TranscodingMaxAudioChannels);
    }

    // --- §3.C: RequireAvc/RequireNonAnamorphic - mandatory per the invariant de parité exécutable -

    private static DeviceProfile BuildProfileWithAvcRequirement()
    {
        var profile = DeviceProfileFixture.BuildWebClientProfile();
        profile.CodecProfiles =
        [
            .. profile.CodecProfiles,
            new CodecProfile
            {
                Type = CodecType.Video,
                Codec = "h264",
                ApplyConditions = [],
                Conditions =
                [
                    new ProfileCondition(ProfileConditionType.Equals, ProfileConditionValue.IsAvc, "true"),
                ],
            },
        ];
        return profile;
    }

    private static DeviceProfile BuildProfileWithAnamorphicRequirement()
    {
        var profile = DeviceProfileFixture.BuildWebClientProfile();
        profile.CodecProfiles =
        [
            .. profile.CodecProfiles,
            new CodecProfile
            {
                Type = CodecType.Video,
                Codec = "h264",
                ApplyConditions = [],
                Conditions =
                [
                    new ProfileCondition(ProfileConditionType.Equals, ProfileConditionValue.IsAnamorphic, "true"),
                ],
            },
        ];
        return profile;
    }

    [Fact]
    public static void ToStreamInfo_Transcode_CodecProfileRequiresAvc_SetsRequireAvc()
    {
        var plan = BuildPlan(method: PlaybackMethod.Transcode, container: "mp4", videoCodec: "h264", transforms: [TransformKind.TranscodeVideo]);
        var videoStream = BuildVideoStream(codec: "h264");
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = BuildProfileWithAvcRequirement();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.True(streamInfo.RequireAvc);
    }

    [Fact]
    public static void ToStreamInfo_Transcode_CodecProfileRequiresNonAnamorphic_SetsRequireNonAnamorphic()
    {
        var plan = BuildPlan(method: PlaybackMethod.Transcode, container: "mp4", videoCodec: "h264", transforms: [TransformKind.TranscodeVideo]);
        var videoStream = BuildVideoStream(codec: "h264");
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = BuildProfileWithAnamorphicRequirement();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.True(streamInfo.RequireNonAnamorphic);
    }

    [Fact]
    public static void ToStreamInfo_Transcode_NoMatchingCodecProfile_LeavesRequireAvcFalse()
    {
        var plan = BuildPlan(method: PlaybackMethod.Transcode, container: "mp4", videoCodec: "h264", transforms: [TransformKind.TranscodeVideo]);
        var videoStream = BuildVideoStream(codec: "h264");
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = DeviceProfileFixture.BuildWebClientProfile();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.False(streamInfo.RequireAvc);
        Assert.False(streamInfo.RequireNonAnamorphic);
    }

    [Fact]
    public static void ToStreamInfo_DirectStream_DoesNotResolveRequireAvcEvenIfCodecProfileWouldRequireIt()
    {
        // ToUrl only serializes RequireAvc/RequireNonAnamorphic for actual Transcode
        // (StreamInfo.IsDirectStream gate) - a Remux session must not resolve them.
        var plan = BuildPlan(method: PlaybackMethod.Remux, container: "mp4", videoCodec: "h264", transforms: []);
        var videoStream = BuildVideoStream(codec: "h264");
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = BuildProfileWithAvcRequirement();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.False(streamInfo.RequireAvc);
    }

    [Fact]
    public static void ToStreamInfo_Transcode_CodecProfileForDifferentTargetCodec_DoesNotSetRequireAvc()
    {
        // The AVC-requiring CodecProfile only names "h264"; a plan targeting a different codec must
        // not pick it up (ContainsAnyCodec gate).
        var plan = BuildPlan(method: PlaybackMethod.Transcode, container: "mp4", videoCodec: "av1", transforms: [TransformKind.TranscodeVideo]);
        var videoStream = BuildVideoStream(codec: "h264");
        var audioStream = BuildAudioStream();
        var mediaSource = BuildMediaSource(streams: [videoStream, audioStream]);
        var deviceProfile = BuildProfileWithAvcRequirement();
        var context = BuildContext(Guid.NewGuid());

        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, deviceProfile);

        Assert.False(streamInfo.RequireAvc);
    }
}
