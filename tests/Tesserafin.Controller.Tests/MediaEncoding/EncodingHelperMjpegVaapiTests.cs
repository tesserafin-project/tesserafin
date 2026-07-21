using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Streaming;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Xunit;

using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// Regression tests for issue #61: <c>mjpeg_vaapi</c> was selectable as a transcoding encoder but has
/// no rate-control branch in <c>GetVideoBitrateParam</c>, so it fell through to the catch-all and
/// emitted <c>-b:v 0 -maxrate 0 -bufsize 0</c> whenever no target bitrate could be derived.
/// </summary>
public class EncodingHelperMjpegVaapiTests
{
    private const string FfmpegPath = "/usr/bin/ffmpeg";

    [Fact]
    public void GetVideoEncoder_MjpegTranscodeOnInteliHD_SelectsSoftwareEncoder()
    {
        var helper = CreateHelper(out var mediaEncoder);
        StubInteliHDWithMjpegVaapi(mediaEncoder);

        var encoder = helper.GetVideoEncoder(BuildMjpegState(), VaapiOptions());

        Assert.Equal("mjpeg", encoder);
    }

    [Fact]
    public void GetProgressiveVideoArguments_MjpegTranscodeOnInteliHD_NeverUsesVaapiNorNullBitrate()
    {
        var helper = CreateHelper(out var mediaEncoder);
        StubInteliHDWithMjpegVaapi(mediaEncoder);

        var args = BuildProgressiveVideoArguments(helper);

        // The transcode job must be handed to the software MJPEG encoder.
        Assert.Contains("-codec:v:0 mjpeg ", args, StringComparison.Ordinal);
        Assert.DoesNotContain("mjpeg_vaapi", args, StringComparison.Ordinal);

        // And it must never carry the catch-all's null rate control.
        Assert.DoesNotContain("-b:v 0", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-maxrate 0", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-bufsize 0", args, StringComparison.Ordinal);
    }

    [Fact]
    public void GetProgressiveVideoArguments_MjpegTranscodeOnInteliHD_AddsNoGlobalQuality()
    {
        var helper = CreateHelper(out var mediaEncoder);
        StubInteliHDWithMjpegVaapi(mediaEncoder);

        var args = BuildProgressiveVideoArguments(helper);

        // No quality target is specified anywhere in the configuration, so none may be invented.
        Assert.DoesNotContain("global_quality", args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetVideoEncoder_MjpegImageExtractionOnInteliHD_KeepsVaapiEncoder()
    {
        // Image extraction never goes through GetVideoBitrateParam and already carries an explicit,
        // validated -global_quality:v. Its behavior is deliberately unchanged.
        var helper = CreateHelper(out var mediaEncoder);
        StubInteliHDWithMjpegVaapi(mediaEncoder);
        var state = BuildMjpegState();
        state.EncoderUsage = VideoEncoderUsage.ImageExtraction;

        Assert.Equal("mjpeg_vaapi", helper.GetVideoEncoder(state, VaapiOptions()));
    }

    [Fact]
    public async Task SoftwareMjpegEncoder_IsAcceptedByRealFfmpeg()
    {
        Assert.SkipUnless(File.Exists(FfmpegPath), $"requires {FfmpegPath}");

        // The generator tests above prove the fallback is selected. Only a real encode proves the
        // fallback is actually usable, i.e. that "no hardware MJPEG" still means "MJPEG works".
        var outputPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mjpeg");
        var arguments = $"-hide_banner -f lavfi -i testsrc=d=1:s=128x128 -c:v mjpeg -f mjpeg -y \"{outputPath}\"";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(FfmpegPath, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            var cancellationToken = TestContext.Current.CancellationToken;
            process.Start();
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdErr = await stdErrTask;
            await stdOutTask;

            Assert.True(process.ExitCode == 0, $"Real ffmpeg rejected the software mjpeg encoder:\n{stdErr}");
            Assert.True(new FileInfo(outputPath).Length > 0, "Software mjpeg encoder produced an empty file.");
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string BuildProgressiveVideoArguments(EncodingHelper helper)
    {
        var state = BuildMjpegState();
        var options = VaapiOptions();
        var videoCodec = helper.GetVideoEncoder(state, options);

        return helper.GetProgressiveVideoArguments(state, options, videoCodec, EncoderPreset.veryfast);
    }

    private static EncodingOptions VaapiOptions()
        => new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, EnableHardwareEncoding = true };

    private static void StubInteliHDWithMjpegVaapi(Mock<IMediaEncoder> mediaEncoder)
    {
        mediaEncoder.Setup(m => m.SupportsEncoder("mjpeg_vaapi")).Returns(true);
        mediaEncoder.Setup(m => m.IsVaapiDeviceInteliHD).Returns(true);
        mediaEncoder.Setup(m => m.IsVaapiDeviceInteli965).Returns(false);
    }

    private static EncodingJobInfo BuildMjpegState()
    {
        var video = new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 320, Height = 240 };

        return new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            MediaSource = new MediaSourceInfo
            {
                Container = "mkv",
                MediaStreams = new List<MediaStream> { video },
                VideoType = VideoType.VideoFile,
            },
            VideoStream = video,
            VideoType = VideoType.VideoFile,
            BaseRequest = new VideoRequestDto(),
            IsVideoRequest = true,
            IsInputVideo = true,
            MediaPath = "/tmp/input.mkv",
            OutputVideoCodec = "mjpeg",

            // Exactly what GetVideoBitrateParamValue produces when neither the request nor the
            // source stream carries a usable bitrate.
            OutputVideoBitrate = 0,
        };
    }

    private static EncodingHelper CreateHelper(out Mock<IMediaEncoder> mediaEncoder)
    {
        var appPaths = Mock.Of<IApplicationPaths>();
        mediaEncoder = new Mock<IMediaEncoder>();
        var subtitleEncoder = new Mock<ISubtitleEncoder>();
        var config = new Mock<IConfiguration>();
        var configurationManager = new Mock<IConfigurationManager>();
        var pathManager = new Mock<IPathManager>();

        return new EncodingHelper(
            appPaths,
            mediaEncoder.Object,
            subtitleEncoder.Object,
            config.Object,
            configurationManager.Object,
            pathManager.Object);
    }
}
