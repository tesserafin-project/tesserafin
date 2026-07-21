using Tesserafin.Model.Dlna;
using Xunit;

namespace Tesserafin.Playback.Dlna.Tests;

/// <summary>
/// Tests for <see cref="PlaybackConstraintsMapper"/>: verifies the field-for-field projection,
/// including the two fields with no direct legacy equivalent (<c>AllowTranscoding</c>, always
/// <see langword="true"/>; <c>StartTimeTicks</c>, always <c>0</c>).
/// </summary>
public static class PlaybackConstraintsMapperTests
{
    private static MediaOptions BuildOptions() => new()
    {
        EnableDirectPlay = true,
        EnableDirectStream = false,
        AllowVideoStreamCopy = true,
        AllowAudioStreamCopy = false,
        AlwaysBurnInSubtitleWhenTranscoding = true,
        MaxBitrate = 20_000_000,
        MaxAudioChannels = 6,
        AudioStreamIndex = 1,
        SubtitleStreamIndex = 2,
        Profile = new DeviceProfile(),
    };

    [Fact]
    public static void ToConstraints_MapsDirectPlayAndDirectStreamFlags()
    {
        var constraints = PlaybackConstraintsMapper.ToConstraints(BuildOptions());

        Assert.True(constraints.AllowDirectPlay);
        Assert.False(constraints.AllowDirectStream);
    }

    [Fact]
    public static void ToConstraints_AlwaysAllowsTranscoding()
    {
        var constraints = PlaybackConstraintsMapper.ToConstraints(BuildOptions());

        Assert.True(constraints.AllowTranscoding);
    }

    [Fact]
    public static void ToConstraints_MapsStreamCopyFlags()
    {
        var constraints = PlaybackConstraintsMapper.ToConstraints(BuildOptions());

        Assert.True(constraints.AllowVideoStreamCopy);
        Assert.False(constraints.AllowAudioStreamCopy);
    }

    [Fact]
    public static void ToConstraints_MapsBitrateAndChannelLimits()
    {
        var constraints = PlaybackConstraintsMapper.ToConstraints(BuildOptions());

        Assert.Equal(20_000_000, constraints.MaxBitrate);
        Assert.Equal(6, constraints.MaxAudioChannels);
    }

    [Fact]
    public static void ToConstraints_MapsPreferredStreamIndices()
    {
        var constraints = PlaybackConstraintsMapper.ToConstraints(BuildOptions());

        Assert.Equal(1, constraints.PreferredAudioStreamIndex);
        Assert.Equal(2, constraints.PreferredSubtitleStreamIndex);
    }

    [Fact]
    public static void ToConstraints_MapsAlwaysBurnInSubtitleWhenTranscoding()
    {
        var constraints = PlaybackConstraintsMapper.ToConstraints(BuildOptions());

        Assert.True(constraints.AlwaysBurnInSubtitleWhenTranscoding);
    }

    [Fact]
    public static void ToConstraints_AlwaysProjectsZeroStartTimeTicks()
    {
        var constraints = PlaybackConstraintsMapper.ToConstraints(BuildOptions());

        Assert.Equal(0, constraints.StartTimeTicks);
    }
}
