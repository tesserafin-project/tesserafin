using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Reefin.Controller.Tests.MediaEncoding;

/// <summary>
/// Fixture below is the verbatim stdout of:
/// <c>ffmpeg -hide_banner -nostats -f lavfi -i "testsrc=duration=1:size=64x64:rate=10" -progress pipe:1 -f null -</c>
/// against real ffmpeg 6.1.1, not a hand-written guess of the format.
/// </summary>
public class FfmpegProgressParserTests
{
    private static readonly string[] _block1 =
    [
        "frame=0",
        "fps=0.00",
        "stream_0_0_q=-0.0",
        "bitrate=N/A",
        "total_size=0",
        "out_time_us=0",
        "out_time_ms=0",
        "out_time=00:00:00.000000",
        "dup_frames=0",
        "drop_frames=0",
        "speed=N/A",
        "progress=continue",
    ];

    private static readonly string[] _block2 =
    [
        "frame=10",
        "fps=0.00",
        "stream_0_0_q=-0.0",
        "bitrate=N/A",
        "total_size=N/A",
        "out_time_us=900000",
        "out_time_ms=900000",
        "out_time=00:00:00.900000",
        "dup_frames=0",
        "drop_frames=0",
        "speed= 292x",
        "progress=end",
    ];

    [Fact]
    public void ConsumeLine_RealFirstBlock_EmitsUpdateOnlyAtProgressLine()
    {
        var parser = new FfmpegProgressParser();

        for (var i = 0; i < _block1.Length - 1; i++)
        {
            Assert.Null(parser.ConsumeLine(_block1[i]));
        }

        var update = parser.ConsumeLine(_block1[^1]);

        Assert.NotNull(update);
        Assert.Equal(0, update!.FrameCount);
        Assert.Equal(0f, update.Fps);
        Assert.Equal(0, update.TotalSizeBytes);
        Assert.Equal(0, update.OutTimeMicroseconds);
        Assert.Null(update.Speed); // "N/A" - unparseable, correctly reported as unknown
        Assert.False(update.IsEnd);
    }

    [Fact]
    public void ConsumeLine_RealSecondBlock_ParsesFinalValuesAndDetectsEnd()
    {
        var parser = new FfmpegProgressParser();
        FfmpegProgressUpdate? update = null;
        foreach (var line in _block2)
        {
            update = parser.ConsumeLine(line) ?? update;
        }

        Assert.NotNull(update);
        Assert.Equal(10, update!.FrameCount);
        Assert.Equal(900000, update.OutTimeMicroseconds);
        Assert.Equal(292f, update.Speed); // "speed= 292x" - leading space and trailing 'x' both stripped
        Assert.Null(update.TotalSizeBytes); // "N/A" this block - must not leak the previous block's total_size=0
        Assert.True(update.IsEnd);
    }

    [Fact]
    public void ConsumeLine_TwoBlocksBackToBack_DoesNotLeakStateBetweenBlocks()
    {
        var parser = new FfmpegProgressParser();
        FfmpegProgressUpdate? first = null;
        foreach (var line in _block1)
        {
            first = parser.ConsumeLine(line) ?? first;
        }

        FfmpegProgressUpdate? second = null;
        foreach (var line in _block2)
        {
            second = parser.ConsumeLine(line) ?? second;
        }

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.FrameCount, second!.FrameCount);
        Assert.NotEqual(first.OutTimeMicroseconds, second.OutTimeMicroseconds);
    }

    [Fact]
    public void ConsumeLine_LineWithoutEqualsSign_IsIgnored()
    {
        var parser = new FfmpegProgressParser();

        Assert.Null(parser.ConsumeLine("not a key value line"));
    }
}
