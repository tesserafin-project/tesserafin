using System.Collections.Generic;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Engine.Tests;

/// <summary>
/// Unit tests pinning the phase-1 engine's decision logic: source selection order, source
/// preference (direct play over remux across sources), default/preferred stream selection, the
/// no-rescue limitation, and audio-only direct play.
/// </summary>
public static class PlaybackEngineTests
{
    [Fact]
    public static void Decide_SkipsUnplayableSource_SelectsNextDirectPlaySource()
    {
        // source[0]: unsupported container (mkv) AND unsupported video codec (hevc) -> not even
        // remuxable, since remux requires videoOk.
        var unplayable = EngineTestFixtures.Source(
            "source-0",
            "mkv",
            videoStreams: [EngineTestFixtures.VideoStream(0, "hevc")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        // source[1]: everything the client supports -> direct play.
        var playable = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]),
            [unplayable, playable],
            EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Equal("source-1", decision.SelectedSource);
    }

    [Fact]
    public static void Decide_PrefersDirectPlayOverRemux_EvenFromALaterSource()
    {
        // source[0]: remuxable - container mismatch (mkv), but codecs are copyable.
        var remuxable = EngineTestFixtures.Source(
            "source-0",
            "mkv",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        // source[1]: direct-plays outright.
        var directPlayable = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            videoStreams: [EngineTestFixtures.VideoStream(0, "h264")],
            audioStreams: [EngineTestFixtures.AudioStream(1, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Video),
            EngineTestFixtures.Capabilities(["mp4"], ["h264"], ["aac"]),
            [remuxable, directPlayable],
            EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Equal("source-1", decision.SelectedSource);
    }

    [Fact]
    public static void Decide_NoPreferredAudio_SelectsDefaultStream()
    {
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            audioStreams:
            [
                EngineTestFixtures.AudioStream(1, "aac", isDefault: false),
                EngineTestFixtures.AudioStream(2, "aac", isDefault: true),
            ]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Audio),
            EngineTestFixtures.Capabilities(["mp4"], [], ["aac"]),
            [source],
            EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(2, decision.SelectedStreams.Audio);
    }

    [Fact]
    public static void Decide_PreferredAudioPresent_SelectsPreferredStream()
    {
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            audioStreams:
            [
                EngineTestFixtures.AudioStream(1, "aac", isDefault: false),
                EngineTestFixtures.AudioStream(2, "aac", isDefault: true),
            ]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Audio),
            EngineTestFixtures.Capabilities(["mp4"], [], ["aac"]),
            [source],
            EngineTestFixtures.Constraints(preferredAudioStreamIndex: 1));

        Assert.True(decision.IsViable);
        Assert.Equal(1, decision.SelectedStreams.Audio);
    }

    [Fact]
    public static void Decide_DefaultAudioUnsupported_DoesNotRescueFromAlternateStream()
    {
        // The default stream (index 1) is DTS, which the client cannot decode outright. An
        // alternate stream (index 2) is AAC, which the client could direct play - but the engine
        // does not search alternate audio streams to rescue playability: it acts on the default
        // stream, transcoding it to AAC (phase 2), rather than switching to the alternate.
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            audioStreams:
            [
                EngineTestFixtures.AudioStream(1, "dts", isDefault: true),
                EngineTestFixtures.AudioStream(2, "aac", isDefault: false),
            ]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Audio),
            EngineTestFixtures.Capabilities(["mp4"], [], ["aac"]),
            [source],
            EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal(1, decision.SelectedStreams.Audio);
    }

    [Fact]
    public static void Decide_AudioOnlyRequest_DirectPlaysWithNoSelectedVideo()
    {
        var source = EngineTestFixtures.Source(
            "source-1",
            "mp4",
            audioStreams: [EngineTestFixtures.AudioStream(0, "aac", isDefault: true)]);

        var engine = new PlaybackEngine();
        var decision = engine.Decide(
            EngineTestFixtures.Context(MediaKind.Audio),
            EngineTestFixtures.Capabilities(["mp4"], [], ["aac"]),
            [source],
            EngineTestFixtures.Constraints());

        Assert.True(decision.IsViable);
        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Null(decision.SelectedStreams.Video);
        Assert.Equal(0, decision.SelectedStreams.Audio);
    }
}
