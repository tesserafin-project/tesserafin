using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// Each fixture line below was captured verbatim from real ffmpeg 6.1.1 stderr output by
/// deliberately triggering the failure (see the FfmpegErrorClassifier remarks), not written from
/// memory of ffmpeg's message format.
/// </summary>
public class FfmpegErrorClassifierTests
{
    [Theory]
    [InlineData("Error opening input file /nonexistent/file.mkv.")]
    [InlineData("[in#0 @ 0x59d7808a7d40] Error opening input: No such file or directory")]
    public void Classify_InvalidInputSamples_ReturnsInvalidInput(string line)
    {
        Assert.Equal(FfmpegErrorCategory.InvalidInput, FfmpegErrorClassifier.Classify(line));
    }

    [Fact]
    public void Classify_PermissionDeniedSample_ReturnsPermissionDenied()
    {
        const string Line = "[out#0/mp4 @ 0x57da690d5f00] Error opening output /root/no_permission.mp4: Permission denied";

        Assert.Equal(FfmpegErrorCategory.PermissionDenied, FfmpegErrorClassifier.Classify(Line));
    }

    [Theory]
    [InlineData("[vost#0:0 @ 0x575c2cc5d2c0] Unknown encoder 'totally_bogus_codec'")]
    [InlineData("Error opening output files: Encoder not found")]
    public void Classify_UnsupportedCodecSamples_ReturnsUnsupportedCodec(string line)
    {
        Assert.Equal(FfmpegErrorCategory.UnsupportedCodec, FfmpegErrorClassifier.Classify(line));
    }

    [Theory]
    [InlineData("[AVHWDeviceContext @ 0x5ebf1cef1d80] No VA display found for device /dev/dri/nonexistent.")]
    [InlineData("Device creation failed: -22.")]
    [InlineData("Failed to set value 'vaapi=va:/dev/dri/nonexistent' for option 'init_hw_device': Invalid argument")]
    public void Classify_DeviceInitializationFailedSamples_ReturnsDeviceInitializationFailed(string line)
    {
        Assert.Equal(FfmpegErrorCategory.DeviceInitializationFailed, FfmpegErrorClassifier.Classify(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("frame=   10 fps=0.0 q=-0.0 Lsize=N/A time=00:00:00.90 bitrate=N/A speed= 292x")]
    [InlineData("Stream mapping:")]
    public void Classify_NonErrorLines_ReturnsUnknown(string line)
    {
        Assert.Equal(FfmpegErrorCategory.Unknown, FfmpegErrorClassifier.Classify(line));
    }
}
