using Reefin.Model.Dlna;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Dlna.Tests;

/// <summary>
/// Tests for <see cref="ReverseConstraintsMapper"/>: verifies the field-for-field application onto
/// an existing <see cref="MediaOptions"/>, including the fields with no legacy sink at all
/// (<c>AllowTranscoding</c>, <c>SubtitleMode</c>, <c>PreferredSubtitleLanguages</c>,
/// <c>StartTimeTicks</c>), which must be left untouched by <see cref="ReverseConstraintsMapper.ApplyTo"/>.
/// </summary>
public static class ReverseConstraintsMapperTests
{
    private static readonly PlaybackConstraints Constraints = new(
        AllowDirectPlay: false,
        AllowDirectStream: true,
        AllowTranscoding: true,
        AllowVideoStreamCopy: false,
        AllowAudioStreamCopy: true,
        MaxBitrate: 15_000_000,
        MaxAudioChannels: 6,
        PreferredAudioStreamIndex: 2,
        PreferredSubtitleStreamIndex: 3,
        SubtitleMode: SubtitlePlaybackMode.Always,
        PreferredSubtitleLanguages: ["eng"],
        AlwaysBurnInSubtitleWhenTranscoding: true,
        StartTimeTicks: 5_000);

    private static MediaOptions BuildOptions() => new() { Profile = new DeviceProfile() };

    [Fact]
    public static void ApplyTo_MapsDirectPlayAndDirectStreamFlags()
    {
        var options = BuildOptions();

        ReverseConstraintsMapper.ApplyTo(options, Constraints);

        Assert.False(options.EnableDirectPlay);
        Assert.True(options.EnableDirectStream);
    }

    [Fact]
    public static void ApplyTo_MapsStreamCopyFlags()
    {
        var options = BuildOptions();

        ReverseConstraintsMapper.ApplyTo(options, Constraints);

        Assert.False(options.AllowVideoStreamCopy);
        Assert.True(options.AllowAudioStreamCopy);
    }

    [Fact]
    public static void ApplyTo_MapsBitrateAndChannelLimits()
    {
        var options = BuildOptions();

        ReverseConstraintsMapper.ApplyTo(options, Constraints);

        Assert.Equal(15_000_000, options.MaxBitrate);
        Assert.Equal(6, options.MaxAudioChannels);
    }

    [Fact]
    public static void ApplyTo_MapsPreferredStreamIndices()
    {
        var options = BuildOptions();

        ReverseConstraintsMapper.ApplyTo(options, Constraints);

        Assert.Equal(2, options.AudioStreamIndex);
        Assert.Equal(3, options.SubtitleStreamIndex);
    }

    [Fact]
    public static void ApplyTo_MapsAlwaysBurnInSubtitleWhenTranscoding()
    {
        var options = BuildOptions();

        ReverseConstraintsMapper.ApplyTo(options, Constraints);

        Assert.True(options.AlwaysBurnInSubtitleWhenTranscoding);
    }

    [Fact]
    public static void ApplyTo_DoesNotTouchFieldsWithNoLegacySink()
    {
        // AllowTranscoding, SubtitleMode, PreferredSubtitleLanguages, and StartTimeTicks have no
        // MediaOptions field at all - applying constraints must not throw or otherwise fail trying
        // to project them, and pre-existing MediaOptions state they might have overlapped with
        // (there is none - this just documents the absence) is left alone.
        var options = BuildOptions();
        options.ForceDirectPlay = true;

        ReverseConstraintsMapper.ApplyTo(options, Constraints);

        Assert.True(options.ForceDirectPlay);
    }
}
