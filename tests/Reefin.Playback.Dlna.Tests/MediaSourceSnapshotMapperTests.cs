using System.Linq;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Model.MediaInfo;
using Xunit;

namespace Reefin.Playback.Dlna.Tests;

/// <summary>
/// Tests for <see cref="MediaSourceSnapshotMapper"/>: verifies stream bucketing by type and the
/// per-field projection rules (video range string, framerate fallback, subtitle format, pass-through
/// support flags).
/// </summary>
public static class MediaSourceSnapshotMapperTests
{
    private static MediaSourceInfo BuildSource()
    {
        return new MediaSourceInfo
        {
            Id = "source-1",
            Container = "mkv",
            Protocol = MediaProtocol.File,
            Bitrate = 15_000_000,
            RunTimeTicks = 72_000_000_000,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Profile = "high",
                    Level = 41,
                    Width = 1920,
                    Height = 1080,
                    BitDepth = 8,
                    RealFrameRate = null,
                    AverageFrameRate = 23.976f,
                    BitRate = 12_000_000,
                    IsAnamorphic = false,
                    IsInterlaced = false,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    SampleRate = 48000,
                    BitDepth = 16,
                    BitRate = 192_000,
                    Language = "eng",
                    IsDefault = true,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = 2,
                    Codec = "srt",
                    IsExternal = true,
                    IsForced = false,
                    IsDefault = false,
                    Language = "eng",
                },
            ],
        };
    }

    [Fact]
    public static void ToSnapshot_BucketsStreamsByType()
    {
        var snapshot = MediaSourceSnapshotMapper.ToSnapshot(BuildSource());

        Assert.Single(snapshot.VideoStreams);
        Assert.Single(snapshot.AudioStreams);
        Assert.Single(snapshot.SubtitleStreams);
    }

    [Fact]
    public static void ToSnapshot_ProjectsVideoRangeAsString()
    {
        var snapshot = MediaSourceSnapshotMapper.ToSnapshot(BuildSource());

        Assert.Equal("SDR", snapshot.VideoStreams.Single().VideoRange);
    }

    [Fact]
    public static void ToSnapshot_FallsBackToAverageFrameRateWhenRealFrameRateIsNull()
    {
        var snapshot = MediaSourceSnapshotMapper.ToSnapshot(BuildSource());

        Assert.Equal(23.976, snapshot.VideoStreams.Single().Framerate!.Value, precision: 3);
    }

    [Fact]
    public static void ToSnapshot_ProjectsSubtitleFormatFromCodec()
    {
        var snapshot = MediaSourceSnapshotMapper.ToSnapshot(BuildSource());

        Assert.Equal("srt", snapshot.SubtitleStreams.Single().Format);
    }

    [Fact]
    public static void ToSnapshot_PassesThroughSupportFlags()
    {
        var snapshot = MediaSourceSnapshotMapper.ToSnapshot(BuildSource());

        Assert.True(snapshot.SupportsDirectPlay);
        Assert.False(snapshot.SupportsDirectStream);
        Assert.True(snapshot.SupportsTranscoding);
    }
}
