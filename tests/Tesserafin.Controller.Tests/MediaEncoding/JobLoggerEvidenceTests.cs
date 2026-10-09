using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// The stderr reader keeps every category it recognised, not only the last one: a decision that
/// saw "device lost" must also be able to see that the input failed first (tesserafin#119).
/// </summary>
public class JobLoggerEvidenceTests
{
    private static async Task<JobLogger> Read(string stderr)
    {
        var logger = new JobLogger(NullLogger.Instance);
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr)));
        await logger.StartStreamingLog(new EncodingJobInfo(TranscodingJobType.Hls) { BaseRequest = new BaseEncodingJobOptions() }, reader, new MemoryStream());
        return logger;
    }

    [Fact]
    public async Task KeepsEveryDistinctCategory_InTheOrderFirstSeen()
    {
        var logger = await Read(
            "Press [q] to stop, [?] for help\n"
            + "amdgpu: The CS has been rejected, see dmesg for more information (-19).\n"
            + "amdgpu: The CS has cancelled because the context is lost. This context is innocent.\n"
            + "[in#0 @ 0x1] Error opening input: No such file or directory\n");

        Assert.Equal(
            [FfmpegErrorCategory.HardwareDeviceLost, FfmpegErrorCategory.InvalidInput],
            logger.GetDetectedErrorCategories());
    }

    [Fact]
    public async Task OutputWithNothingRecognised_YieldsNoCategory()
    {
        var logger = await Read("Press [q] to stop, [?] for help\nframe=   10 fps=0.0 q=-0.0 size=N/A time=00:00:00.90 bitrate=N/A speed= 292x\n");

        Assert.Empty(logger.GetDetectedErrorCategories());
        Assert.Null(logger.UnsupportedCodecName);
    }

    [Fact]
    public async Task UnknownEncoderLine_KeepsTheCodecItNamed()
    {
        var logger = await Read("[vost#0:0 @ 0x1] Unknown encoder 'h264_vaapi'\n");

        Assert.Equal([FfmpegErrorCategory.UnsupportedCodec], logger.GetDetectedErrorCategories());
        Assert.Equal("h264_vaapi", logger.UnsupportedCodecName);
    }
}
