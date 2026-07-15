using System;
using System.Collections.Generic;
using System.Linq;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Model.Session;
using Reefin.Playback.Decision;
using Xunit;
using LegacySubtitleDeliveryMethod = Reefin.Model.Dlna.SubtitleDeliveryMethod;

namespace Reefin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Unit tests for <see cref="PlaybackSessionResponseMapper"/>: hand-built <see cref="PlaybackSession"/>
/// values (mirroring <c>LegacyDecisionProjectorTests</c>'s style/fixtures), checked against the
/// expected <see cref="PlaybackSessionResponse"/>. Covers method mapping, best-effort transform/
/// reason derivation (including the PR111c tonemap/subtitle-conversion detection), and the
/// documented approximations (unknown video stream index, unpopulated total/audio bitrate).
/// </summary>
public sealed class PlaybackSessionResponseMapperTests
{
    [Fact]
    public void Map_DirectPlay_MapsMethodOutputAndNoTransforms()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.DirectPlay, 0, "mp4", audioIndex: 1);
        var session = BuildSession(PlayMethod.DirectPlay, 0, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(PlaybackMethod.DirectPlay, response.Method);
        Assert.Empty(response.Transforms);
        Assert.Empty(response.Reasons);
        Assert.Equal("mp4", response.Output.Container);
        Assert.Equal("h264", response.Output.VideoCodec);
        Assert.Equal("aac", response.Output.AudioCodec);
        Assert.Equal(1, response.SelectedStreams.Audio);

        // Legacy StreamInfo does not expose a reliable "selected video stream index": always null.
        Assert.Null(response.SelectedStreams.Video);

        // No subtitle index was passed to BuildStreamInfo: legacy positively selected none.
        Assert.Null(response.SelectedStreams.Subtitle);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, response.DecisionVersion);
    }

    [Fact]
    public void Map_DirectStream_NormalizesToRemuxMethodWithRemuxContainerTransform()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.DirectStream, TranscodeReason.ContainerNotSupported, "mkv");
        var session = BuildSession(PlayMethod.DirectStream, TranscodeReason.ContainerNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(PlaybackMethod.Remux, response.Method);
        Assert.Equal(new[] { TransformKind.RemuxContainer }, response.Transforms);
        Assert.Equal(new[] { ReasonCode.ContainerNotSupported }, response.Reasons);
    }

    [Theory]
    [InlineData(TranscodeReason.VideoCodecNotSupported, ReasonCode.VideoCodecNotSupported)]
    [InlineData(TranscodeReason.AudioCodecNotSupported, ReasonCode.AudioCodecNotSupported)]
    [InlineData(TranscodeReason.SubtitleCodecNotSupported, ReasonCode.SubtitleCodecNotSupported)]
    [InlineData(TranscodeReason.VideoRangeTypeNotSupported, ReasonCode.VideoRangeTypeNotSupported)]
    public void Map_Transcode_MirrorsEachTranscodeReasonToItsOneToOneReasonCode(TranscodeReason reason, ReasonCode expectedCode)
    {
        var streamInfo = BuildStreamInfo(PlayMethod.Transcode, reason, "mp4");
        var session = BuildSession(PlayMethod.Transcode, reason, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Contains(expectedCode, response.Reasons);
    }

    [Fact]
    public void Map_Transcode_AudioCodecReason_DerivesTranscodeAudioOnly()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "mp4");
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(new[] { TransformKind.TranscodeAudio }, response.Transforms);
    }

    [Fact]
    public void Map_Transcode_AudioChannelsReason_DerivesTranscodeAudioAndDownmix()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.AudioChannelsNotSupported, "mp4");
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.AudioChannelsNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(
            new HashSet<TransformKind> { TransformKind.TranscodeAudio, TransformKind.Downmix },
            response.Transforms.ToHashSet());
    }

    [Fact]
    public void Map_Transcode_VideoRangeReason_DerivesTranscodeVideoAndTonemap()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported, "mp4");
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(
            new HashSet<TransformKind> { TransformKind.TranscodeVideo, TransformKind.Tonemap },
            response.Transforms.ToHashSet());
    }

    [Fact]
    public void Map_Transcode_SubtitleReason_DerivesBurnInSubtitle()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.SubtitleCodecNotSupported, "mp4");
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.SubtitleCodecNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(new[] { TransformKind.BurnInSubtitle }, response.Transforms);
    }

    [Fact]
    public void Map_HdrSourceTranscodedForUnsupportedCodecToSdr_DerivesTonemapEvenWithoutRangeReason()
    {
        // PR111c: forced by video codec, not range - legacy sets no VideoRangeTypeNotSupported bit,
        // so Tonemap must come from DetectedTonemap's output-state comparison instead: the source
        // stream carries an HDR ColorTransfer (via MediaStream.VideoRangeType), while the chosen
        // output codec's declared range (StreamInfo.TargetVideoRangeType, resolved off
        // VideoCodecs/StreamOptions for a non-direct-stream method) is forced to SDR here.
        var streamInfo = BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported, "mp4", colorTransfer: "smpte2084");
        streamInfo.VideoCodecs = ["h264"];
        streamInfo.StreamOptions["h264-rangetype"] = "SDR";
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Contains(TransformKind.Tonemap, response.Transforms);
        Assert.Contains(TransformKind.TranscodeVideo, response.Transforms);
    }

    [Fact]
    public void Map_Transcode_SubtitleFormatDiffersFromSource_DerivesConvertSubtitleWithNoReasonBit()
    {
        // PR111c: a real subtitle text-format conversion carries no reason bit at all.
        var streamInfo = BuildStreamInfo(
            PlayMethod.Transcode,
            TranscodeReason.ContainerNotSupported,
            "mp4",
            subtitleIndex: 2,
            subtitleDeliveryMethod: LegacySubtitleDeliveryMethod.External,
            subtitleCodec: "srt",
            subtitleFormat: "vtt");
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.ContainerNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Contains(TransformKind.ConvertSubtitle, response.Transforms);
        Assert.Equal("vtt", response.Output.SubtitleFormat);
        Assert.NotNull(response.SelectedStreams.Subtitle);
        Assert.Equal(2, response.SelectedStreams.Subtitle!.Index);
        Assert.Equal(Reefin.Playback.Decision.SubtitleDeliveryMethod.External, response.SelectedStreams.Subtitle.Delivery);
    }

    [Fact]
    public void Map_SubtitleEncodeDeliveryMethod_MapsToBurnDeliveryOnSelectedSubtitle()
    {
        var streamInfo = BuildStreamInfo(
            PlayMethod.Transcode,
            TranscodeReason.SubtitleCodecNotSupported,
            "mp4",
            subtitleIndex: 3,
            subtitleDeliveryMethod: LegacySubtitleDeliveryMethod.Encode,
            subtitleCodec: "srt",
            subtitleFormat: "srt");
        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.SubtitleCodecNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.NotNull(response.SelectedStreams.Subtitle);
        Assert.Equal(Reefin.Playback.Decision.SubtitleDeliveryMethod.Burn, response.SelectedStreams.Subtitle!.Delivery);

        // Burn-in re-encodes the video, not a subtitle format conversion for delivery.
        Assert.DoesNotContain(TransformKind.ConvertSubtitle, response.Transforms);
    }

    [Fact]
    public void Map_NoStreamInfo_ReturnsEmptyOutputAndNoSelectedStreams()
    {
        var session = BuildSession(PlayMethod.DirectPlay, 0, streamInfo: null);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(OutputSpec.Empty, response.Output);
        Assert.Equal(SelectedStreams.None, response.SelectedStreams);
        Assert.Empty(response.Transforms);
        Assert.Empty(response.Reasons);
    }

    [Fact]
    public void Map_DoesNotPopulateTotalOrAudioBitrate_ToAvoidFabricatingAValue()
    {
        var streamInfo = BuildStreamInfo(PlayMethod.Transcode, TranscodeReason.VideoBitrateNotSupported, "mp4");
        streamInfo.VideoBitrate = 4_000_000;

        var session = BuildSession(PlayMethod.Transcode, TranscodeReason.VideoBitrateNotSupported, streamInfo);

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Null(response.Output.TotalBitrate);
        Assert.Null(response.Output.AudioBitrate);
    }

    [Fact]
    public void Map_AudioKind_MapsToMediaKindAudio()
    {
        var session = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Audio,
            null,
            null,
            new PlaybackPlan(PlayMethod.DirectPlay, default),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1));

        var response = PlaybackSessionResponseMapper.Map(session);

        Assert.Equal(MediaKind.Audio, response.Kind);
        Assert.Equal(session.Id.Value, response.Id);
        Assert.Equal(session.CreatedAt, response.CreatedAt);
        Assert.Equal(session.UpdatedAt, response.UpdatedAt);
    }

    private static PlaybackSession BuildSession(PlayMethod playMethod, TranscodeReason reasons, StreamInfo? streamInfo)
        => new(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            null,
            null,
            new PlaybackPlan(playMethod, reasons, streamInfo),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1));

    private static StreamInfo BuildStreamInfo(
        PlayMethod playMethod,
        TranscodeReason reasons,
        string container,
        int? audioIndex = null,
        int? subtitleIndex = null,
        LegacySubtitleDeliveryMethod subtitleDeliveryMethod = LegacySubtitleDeliveryMethod.Embed,
        string? colorTransfer = null,
        string? subtitleCodec = null,
        string? subtitleFormat = null)
    {
        var mediaStreams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264", ColorTransfer = colorTransfer },
            new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", IsDefault = true },
        };

        if (subtitleIndex is int idx && subtitleCodec is not null)
        {
            mediaStreams.Add(new MediaStream { Type = MediaStreamType.Subtitle, Index = idx, Codec = subtitleCodec, IsDefault = true });
        }

        var mediaSource = new MediaSourceInfo
        {
            Id = "source-1",
            Container = container,
            MediaStreams = mediaStreams,
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
            SubtitleFormat = subtitleFormat,
            MediaSource = mediaSource,
        };
    }
}
