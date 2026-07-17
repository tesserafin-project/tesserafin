using System;
using System.Collections.Generic;
using Moq;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Data.Enums;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Model.Session;
using Reefin.Playback.Decision;
using Xunit;
using LegacySubtitleDeliveryMethod = Reefin.Model.Dlna.SubtitleDeliveryMethod;

namespace Reefin.Api.Tests.Models.PlaybackSessionDtos;

public class PlaybackSessionStreamDescriptorMapperTests
{
    /// <summary>
    /// Design doc §2.2: <c>Url</c> is a direct, unmodified output of <c>StreamInfo.ToUrl</c> - the
    /// mapper must never build it any other way.
    /// </summary>
    [Fact]
    public void Map_BuildsUrlDirectlyFromStreamInfoToUrl()
    {
        var streamInfo = new StreamInfo
        {
            ItemId = Guid.NewGuid(),
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            MediaSource = new MediaSourceInfo { Id = "source-1" },
        };

        var descriptor = PlaybackSessionStreamDescriptorMapper.Map(streamInfo, servedBy: 1, fallbackReason: null, Mock.Of<ITranscoderSupport>(), accessToken: "token-1");

        Assert.Equal(streamInfo.ToUrl(null, "token-1", null), descriptor.Url);
    }

    [Theory]
    [InlineData(MediaStreamProtocol.hls, StreamingProtocol.Hls)]
    [InlineData(MediaStreamProtocol.http, StreamingProtocol.Http)]
    public void Map_ProjectsProtocol_SameVocabularyAsOutputSpec(MediaStreamProtocol subProtocol, StreamingProtocol expected)
    {
        var streamInfo = new StreamInfo
        {
            ItemId = Guid.NewGuid(),
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            SubProtocol = subProtocol,
            MediaSource = new MediaSourceInfo { Id = "source-1" },
        };

        var descriptor = PlaybackSessionStreamDescriptorMapper.Map(streamInfo, servedBy: 1, fallbackReason: null, Mock.Of<ITranscoderSupport>(), accessToken: null);

        Assert.Equal(expected, descriptor.Protocol);
    }

    [Fact]
    public void Map_PassesThroughServedByAndFallbackReasonVerbatim()
    {
        var streamInfo = new StreamInfo
        {
            ItemId = Guid.NewGuid(),
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            MediaSource = new MediaSourceInfo { Id = "source-1" },
        };

        var served = PlaybackSessionStreamDescriptorMapper.Map(streamInfo, servedBy: 6, fallbackReason: null, Mock.Of<ITranscoderSupport>(), accessToken: null);
        Assert.Equal(6, served.ServedBy);
        Assert.Null(served.FallbackReason);

        var fallback = PlaybackSessionStreamDescriptorMapper.Map(streamInfo, servedBy: PlaybackSessionResponse.LegacyDecisionVersion, fallbackReason: PlaybackLiveFallbackReason.KillSwitch, Mock.Of<ITranscoderSupport>(), accessToken: null);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, fallback.ServedBy);
        Assert.Equal(PlaybackLiveFallbackReason.KillSwitch, fallback.FallbackReason);
    }

    [Fact]
    public void Map_NoSubtitleSelected_SubtitleUrlIsNull()
    {
        var streamInfo = new StreamInfo
        {
            ItemId = Guid.NewGuid(),
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            SubtitleStreamIndex = -1,
            MediaSource = new MediaSourceInfo { Id = "source-1" },
        };

        var descriptor = PlaybackSessionStreamDescriptorMapper.Map(streamInfo, servedBy: 1, fallbackReason: null, Mock.Of<ITranscoderSupport>(), accessToken: null);

        Assert.Null(descriptor.SubtitleUrl);
    }

    /// <summary>
    /// Design doc §2.2: <c>Encode</c>/<c>Embed</c>/<c>Hls</c> subtitle delivery never carries a
    /// <c>SubtitleUrl</c> - only <c>External</c> does. Forces <c>Encode</c> here (transcode +
    /// a device profile whose subtitle profile declares <c>Encode</c>, same recipe as
    /// <c>StreamBuilderTests.GetSubtitleProfile</c>).
    /// </summary>
    [Fact]
    public void Map_SubtitleSelectedButNotExternal_SubtitleUrlIsNull()
    {
        var subtitleStream = new MediaStream { Type = MediaStreamType.Subtitle, Index = 0, Codec = "srt" };
        var mediaSource = new MediaSourceInfo
        {
            Id = "source-1",
            MediaStreams = new List<MediaStream> { subtitleStream },
        };
        var deviceProfile = new DeviceProfile
        {
            SubtitleProfiles = new[] { new SubtitleProfile { Format = "srt", Method = LegacySubtitleDeliveryMethod.Encode } },
        };
        var streamInfo = new StreamInfo
        {
            ItemId = Guid.NewGuid(),
            DeviceProfile = deviceProfile,
            PlayMethod = PlayMethod.Transcode,
            Container = "mkv",
            SubtitleStreamIndex = 0,
            SubtitleDeliveryMethod = LegacySubtitleDeliveryMethod.Encode,
            MediaSource = mediaSource,
        };

        var descriptor = PlaybackSessionStreamDescriptorMapper.Map(streamInfo, servedBy: 1, fallbackReason: null, Mock.Of<ITranscoderSupport>(), accessToken: null);

        Assert.Null(descriptor.SubtitleUrl);
    }

    /// <summary>
    /// Design doc §2.2 (the positive case): a subtitle actually delivered externally - same recipe
    /// <c>StreamBuilderTests.GetSubtitleProfile</c> uses to force
    /// <see cref="LegacySubtitleDeliveryMethod.External"/> (DirectPlay, matching format profile).
    /// </summary>
    [Fact]
    public void Map_ExternalSubtitleSelected_SubtitleUrlIsPopulated()
    {
        var subtitleStream = new MediaStream { Type = MediaStreamType.Subtitle, Index = 0, Codec = "srt" };
        var mediaSource = new MediaSourceInfo
        {
            Id = "source-1",
            MediaStreams = new List<MediaStream> { subtitleStream },
        };
        var deviceProfile = new DeviceProfile
        {
            SubtitleProfiles = new[] { new SubtitleProfile { Format = "srt", Method = LegacySubtitleDeliveryMethod.External } },
        };
        var streamInfo = new StreamInfo
        {
            ItemId = Guid.NewGuid(),
            DeviceProfile = deviceProfile,
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            SubtitleStreamIndex = 0,
            SubtitleDeliveryMethod = LegacySubtitleDeliveryMethod.External,
            MediaSource = mediaSource,
        };

        var descriptor = PlaybackSessionStreamDescriptorMapper.Map(streamInfo, servedBy: 1, fallbackReason: null, Mock.Of<ITranscoderSupport>(), accessToken: "token-1");

        Assert.NotNull(descriptor.SubtitleUrl);
        Assert.Contains("/Subtitles/0/", descriptor.SubtitleUrl, StringComparison.Ordinal);
    }
}
