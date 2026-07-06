using System.Collections.Generic;
using Moq;
using Reefin.Common.Configuration;
using Reefin.Controller.IO;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Controller.Streaming;
using Reefin.Model.Configuration;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Model.MediaInfo;
using Xunit;

using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Reefin.Controller.Tests.MediaEncoding;

/// <summary>
/// Verifies <see cref="TranscodePlanner.CreatePlan"/> stays in lockstep with
/// <see cref="EncodingHelper.GetVideoEncoder"/> across the same backend matrix
/// <see cref="EncodingHelperVideoEncoderSelectionTests"/> locks - the planner delegates rather
/// than re-deriving, so parity holds by construction, but these tests also lock the
/// <see cref="TranscodePlan.IsHardwareEncoder"/> classification, which is new logic PR1 never
/// covered.
/// </summary>
public class TranscodePlannerTests
{
    public static IEnumerable<object[]> H264HwBackends()
    {
        yield return [HardwareAccelerationType.amf, "h264_amf"];
        yield return [HardwareAccelerationType.nvenc, "h264_nvenc"];
        yield return [HardwareAccelerationType.qsv, "h264_qsv"];
        yield return [HardwareAccelerationType.vaapi, "h264_vaapi"];
        yield return [HardwareAccelerationType.videotoolbox, "h264_videotoolbox"];
        yield return [HardwareAccelerationType.v4l2m2m, "h264_v4l2m2m"];
        yield return [HardwareAccelerationType.rkmpp, "h264_rkmpp"];
    }

    [Theory]
    [MemberData(nameof(H264HwBackends))]
    public void CreatePlan_HwSupported_MatchesGetVideoEncoderAndFlagsAsHardware(HardwareAccelerationType hwType, string expectedEncoder)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(expectedEncoder)).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = true };

        var plan = TranscodePlanner.CreatePlan(helper, state, options);

        Assert.Equal(helper.GetVideoEncoder(state, options), plan.SelectedVideoEncoder);
        Assert.Equal(expectedEncoder, plan.SelectedVideoEncoder);
        Assert.True(plan.IsHardwareEncoder);
        Assert.Equal(hwType, plan.RequestedHardwareAccelerationType);
        Assert.Equal("h264", plan.VideoCodec);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.amf)]
    [InlineData(HardwareAccelerationType.nvenc)]
    [InlineData(HardwareAccelerationType.qsv)]
    [InlineData(HardwareAccelerationType.vaapi)]
    [InlineData(HardwareAccelerationType.videotoolbox)]
    [InlineData(HardwareAccelerationType.v4l2m2m)]
    [InlineData(HardwareAccelerationType.rkmpp)]
    public void CreatePlan_EncoderNotAdvertised_MatchesGetVideoEncoderAndFlagsAsSoftware(HardwareAccelerationType hwType)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(It.IsAny<string>())).Returns(false);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = true };

        var plan = TranscodePlanner.CreatePlan(helper, state, options);

        Assert.Equal(helper.GetVideoEncoder(state, options), plan.SelectedVideoEncoder);
        Assert.Equal("libx264", plan.SelectedVideoEncoder);
        Assert.False(plan.IsHardwareEncoder);
    }

    [Fact]
    public void CreatePlan_StreamCopy_MatchesGetVideoEncoderAndFlagsAsSoftware()
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(It.IsAny<string>())).Returns(true);
        var state = BuildVideoFileState();
        state.OutputVideoCodec = string.Empty;
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, EnableHardwareEncoding = true };

        var plan = TranscodePlanner.CreatePlan(helper, state, options);

        Assert.Equal("copy", plan.SelectedVideoEncoder);
        Assert.False(plan.IsHardwareEncoder);
        Assert.Equal(string.Empty, plan.VideoCodec);
    }

    private static EncodingJobInfo BuildVideoFileState()
    {
        var video = new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" };

        return new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            MediaSource = new MediaSourceInfo
            {
                Container = "mkv",
                MediaStreams = [video],
                VideoType = VideoType.VideoFile,
            },
            VideoStream = video,
            OutputVideoCodec = "h264",
            VideoType = VideoType.VideoFile,
            BaseRequest = new VideoRequestDto(),
            IsVideoRequest = true,
            IsInputVideo = true,
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
