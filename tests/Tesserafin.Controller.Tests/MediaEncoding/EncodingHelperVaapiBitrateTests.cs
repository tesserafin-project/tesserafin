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
/// Regression tests for issue #55: h264_vaapi refuses <c>-b:v 0</c> in VBR mode, so every hardware
/// transcode failed whenever no target bitrate could be derived for the job.
/// </summary>
/// <remarks>
/// <para>
/// Root cause: <see cref="EncodingHelper.GetVideoBitrateParamValue"/> returns a non-nullable
/// <c>int</c> and collapses "nothing to derive" to <c>0</c> (<c>Math.Min(bitrate ?? 0, ...)</c>).
/// <c>StreamingHelpers</c> stores that <c>0</c> into <c>OutputVideoBitrate</c>, so the
/// <c>OutputVideoBitrate is null</c> early-out in <c>GetVideoBitrateParam</c> never fires and the
/// VAAPI branch emitted <c>-rc_mode VBR -b:v 0 -maxrate 0 -bufsize 0</c>.
/// </para>
/// <para>
/// Policy when no target bitrate exists: emit no rate-control arguments at all and let VAAPI fall
/// back to its native constant-QP default. This is not an invented quality target - it is the same
/// policy the pre-existing <c>OutputVideoBitrate is null</c> branch already applies, extended to the
/// numerically-equivalent "0" case. Deliberately no explicit <c>-rc_mode CQP</c> with a hand-picked
/// QP, which would be a fabricated target.
/// </para>
/// <para>
/// The guard is scoped to the VAAPI branch only. The software path keeps its exact current output,
/// which <see cref="GetVideoQualityParam_Libx264_NoDerivableBitrate_OutputIsUnchanged"/> locks.
/// </para>
/// <para>
/// These tests exercise the command generator and need no GPU. The one test that does touch
/// hardware is explicitly named and skips when no render node is present.
/// </para>
/// </remarks>
public class EncodingHelperVaapiBitrateTests
{
    private const string VaapiRenderNode = "/dev/dri/renderD128";

    public static TheoryData<string> VaapiEncoders() => new() { "h264_vaapi", "hevc_vaapi", "av1_vaapi" };

    [Theory]
    [MemberData(nameof(VaapiEncoders))]
    public void GetVideoQualityParam_Vaapi_NoDerivableBitrate_NeverEmitsZeroBitrate(string encoder)
    {
        // OutputVideoBitrate = 0 is exactly what GetVideoBitrateParamValue produces when neither
        // the request nor the source stream carries a usable bitrate.
        var param = BuildQualityParam(encoder, outputVideoBitrate: 0);

        Assert.DoesNotContain("-b:v 0", param, StringComparison.Ordinal);
        Assert.DoesNotContain("-maxrate 0", param, StringComparison.Ordinal);
        Assert.DoesNotContain("-bufsize 0", param, StringComparison.Ordinal);

        // No rate control at all - the encoder is left on its native constant-QP default.
        Assert.DoesNotContain("-b:v", param, StringComparison.Ordinal);
        Assert.DoesNotContain("-rc_mode", param, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(VaapiEncoders))]
    public void GetVideoQualityParam_Vaapi_ExplicitUserBitrate_IsPreserved(string encoder)
    {
        const int Bitrate = 3_000_000;

        var param = BuildQualityParam(encoder, outputVideoBitrate: Bitrate);

        // A bitrate the user actually configured must survive untouched.
        Assert.Contains("-b:v 3000000", param, StringComparison.Ordinal);
        Assert.Contains("-rc_mode VBR", param, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(VaapiEncoders))]
    public void GetVideoQualityParam_Vaapi_MaxrateAndBufsizeStayCoherentWithBitrate(string encoder)
    {
        const int Bitrate = 3_000_000;

        var param = BuildQualityParam(encoder, outputVideoBitrate: Bitrate);

        // maxrate tracks the bitrate, bufsize is the documented 2x buffer. Locking the numbers
        // together is what proves the trio can never drift apart (e.g. a bitrate with a 0 bufsize).
        Assert.Contains("-maxrate 3000000", param, StringComparison.Ordinal);
        Assert.Contains("-bufsize 6000000", param, StringComparison.Ordinal);
    }

    [Fact]
    public void GetVideoQualityParam_Libx264_NoDerivableBitrate_OutputIsUnchanged()
    {
        // Characterization lock: the software path must keep byte-identical behavior. libx264
        // tolerates a zero VBV (it means "unconstrained"), so the fix deliberately does NOT touch
        // it. If a future refactor widens the zero-bitrate guard past the VAAPI branch, this fails.
        var param = BuildQualityParam("libx264", outputVideoBitrate: 0, hwaccel: HardwareAccelerationType.none);

        Assert.Contains("-maxrate 0 -bufsize 0", param, StringComparison.Ordinal);
    }

    [Fact]
    public void GetVideoQualityParam_Libx264_ExplicitUserBitrate_IsPreserved()
    {
        var param = BuildQualityParam("libx264", outputVideoBitrate: 3_000_000, hwaccel: HardwareAccelerationType.none);

        Assert.Contains("-maxrate 3000000 -bufsize 6000000", param, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVideoQualityParam_Vaapi_NoDerivableBitrate_ArgumentsAreAcceptedByRealVaapiHardware()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists(VaapiRenderNode), "requires a real VAAPI render node - not present in this environment");

        // The generator test above proves we no longer emit "-b:v 0". Only a real encode proves the
        // resulting argument set - no rate control at all - is one h264_vaapi actually accepts.
        var param = BuildQualityParam("h264_vaapi", outputVideoBitrate: 0).Trim();

        var arguments = $"-hide_banner -init_hw_device vaapi=va:{VaapiRenderNode} -filter_hw_device va -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -vf format=nv12,hwupload -c:v h264_vaapi {param} -f null -";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", arguments)
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

        Assert.True(process.ExitCode == 0, $"Real ffmpeg rejected the generated h264_vaapi arguments '{param}':\n{stdErr}");
    }

    private static string BuildQualityParam(
        string encoder,
        int outputVideoBitrate,
        HardwareAccelerationType hwaccel = HardwareAccelerationType.vaapi)
    {
        var state = BuildState(outputVideoBitrate);
        var options = new EncodingOptions { HardwareAccelerationType = hwaccel, EnableHardwareEncoding = true };

        return CreateHelper().GetVideoQualityParam(state, encoder, options, EncoderPreset.veryfast);
    }

    private static EncodingJobInfo BuildState(int outputVideoBitrate)
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
            OutputVideoBitrate = outputVideoBitrate,
        };
    }

    private static EncodingHelper CreateHelper()
    {
        var appPaths = Mock.Of<IApplicationPaths>();
        var mediaEncoder = new Mock<IMediaEncoder>();
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
