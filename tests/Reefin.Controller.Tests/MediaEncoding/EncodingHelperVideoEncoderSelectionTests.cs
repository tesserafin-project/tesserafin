using System.Collections.Generic;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using Moq;
using Reefin.Common.Configuration;
using Reefin.Model.Configuration;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Model.MediaInfo;
using Xunit;

using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Reefin.Controller.Tests.MediaEncoding;

/// <summary>
/// Characterization tests locking the current hardware-encoder selection matrix in
/// <see cref="EncodingHelper"/> before any transcoding-pipeline refactor. These assert
/// existing behavior only - no functional change is expected to accompany this file.
/// </summary>
public class EncodingHelperVideoEncoderSelectionTests
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

    public static IEnumerable<object[]> H265HwBackends()
    {
        yield return [HardwareAccelerationType.amf, "hevc_amf"];
        yield return [HardwareAccelerationType.nvenc, "hevc_nvenc"];
        yield return [HardwareAccelerationType.qsv, "hevc_qsv"];
        yield return [HardwareAccelerationType.vaapi, "hevc_vaapi"];
        yield return [HardwareAccelerationType.videotoolbox, "hevc_videotoolbox"];
        yield return [HardwareAccelerationType.v4l2m2m, "hevc_v4l2m2m"];
        yield return [HardwareAccelerationType.rkmpp, "hevc_rkmpp"];
    }

    public static IEnumerable<object[]> Av1HwBackends()
    {
        yield return [HardwareAccelerationType.amf, "av1_amf"];
        yield return [HardwareAccelerationType.nvenc, "av1_nvenc"];
        yield return [HardwareAccelerationType.qsv, "av1_qsv"];
        yield return [HardwareAccelerationType.vaapi, "av1_vaapi"];
        yield return [HardwareAccelerationType.videotoolbox, "av1_videotoolbox"];
        yield return [HardwareAccelerationType.v4l2m2m, "av1_v4l2m2m"];
        yield return [HardwareAccelerationType.rkmpp, "av1_rkmpp"];
    }

    [Theory]
    [MemberData(nameof(H264HwBackends))]
    public void GetH264Encoder_HwSupportedOnVideoFile_ReturnsHwEncoder(HardwareAccelerationType hwType, string expected)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(expected)).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = true };

        Assert.Equal(expected, helper.GetH264Encoder(state, options));
    }

    [Theory]
    [MemberData(nameof(H265HwBackends))]
    public void GetH265Encoder_HwSupportedOnVideoFile_ReturnsHwEncoder(HardwareAccelerationType hwType, string expected)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(expected)).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = true };

        Assert.Equal(expected, helper.GetH265Encoder(state, options));
    }

    [Theory]
    [MemberData(nameof(Av1HwBackends))]
    public void GetAv1Encoder_HwSupportedOnVideoFile_ReturnsHwEncoder(HardwareAccelerationType hwType, string expected)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(expected)).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = true };

        Assert.Equal(expected, helper.GetAv1Encoder(state, options));
    }

    [Theory]
    [InlineData(HardwareAccelerationType.amf)]
    [InlineData(HardwareAccelerationType.nvenc)]
    [InlineData(HardwareAccelerationType.qsv)]
    [InlineData(HardwareAccelerationType.vaapi)]
    [InlineData(HardwareAccelerationType.videotoolbox)]
    [InlineData(HardwareAccelerationType.v4l2m2m)]
    [InlineData(HardwareAccelerationType.rkmpp)]
    public void GetH264Encoder_EncoderNotAdvertisedByFfmpeg_FallsBackToSoftware(HardwareAccelerationType hwType)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(It.IsAny<string>())).Returns(false);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = true };

        Assert.Equal("libx264", helper.GetH264Encoder(state, options));
    }

    [Theory]
    [MemberData(nameof(H264HwBackends))]
    public void GetH264Encoder_HardwareEncodingDisabled_FallsBackToSoftwareEvenIfSupported(HardwareAccelerationType hwType, string expected)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(expected)).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = false };

        Assert.Equal("libx264", helper.GetH264Encoder(state, options));
    }

    [Theory]
    [InlineData(VideoType.Iso)]
    [InlineData(VideoType.Dvd)]
    [InlineData(VideoType.BluRay)]
    public void GetH264Encoder_NonVideoFileType_AlwaysSoftware(VideoType videoType)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(It.IsAny<string>())).Returns(true);
        var state = BuildVideoFileState(videoType);
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, EnableHardwareEncoding = true };

        Assert.Equal("libx264", helper.GetH264Encoder(state, options));
    }

    [Fact]
    public void GetH264Encoder_HardwareAccelerationNone_ReturnsSoftware()
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(It.IsAny<string>())).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.none, EnableHardwareEncoding = true };

        Assert.Equal("libx264", helper.GetH264Encoder(state, options));
    }

    [Fact]
    public void GetMjpegEncoder_VaapiWithoutIntelIHD_AlwaysSoftware()
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder("mjpeg_vaapi")).Returns(true);
        mediaEncoder.Setup(m => m.IsVaapiDeviceInteliHD).Returns(false);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, EnableHardwareEncoding = true };

        Assert.Equal("mjpeg", helper.GetVideoEncoder(WithCodec(state, "mjpeg"), options));
    }

    [Fact]
    public void GetMjpegEncoder_VaapiWithIntelIHD_ReturnsHwEncoder()
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder("mjpeg_vaapi")).Returns(true);
        mediaEncoder.Setup(m => m.IsVaapiDeviceInteliHD).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, EnableHardwareEncoding = true };

        Assert.Equal("mjpeg_vaapi", helper.GetVideoEncoder(WithCodec(state, "mjpeg"), options));
    }

    [Theory]
    [InlineData(HardwareAccelerationType.qsv, "mjpeg_qsv")]
    [InlineData(HardwareAccelerationType.videotoolbox, "mjpeg_videotoolbox")]
    [InlineData(HardwareAccelerationType.rkmpp, "mjpeg_rkmpp")]
    public void GetMjpegEncoder_SupportedHwBackend_ReturnsHwEncoder(HardwareAccelerationType hwType, string expected)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(expected)).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = true };

        Assert.Equal(expected, helper.GetVideoEncoder(WithCodec(state, "mjpeg"), options));
    }

    [Theory]
    [InlineData(HardwareAccelerationType.amf)]
    [InlineData(HardwareAccelerationType.nvenc)]
    [InlineData(HardwareAccelerationType.v4l2m2m)]
    public void GetMjpegEncoder_BackendWithoutMjpegSupport_AlwaysSoftware(HardwareAccelerationType hwType)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(It.IsAny<string>())).Returns(true);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = hwType, EnableHardwareEncoding = true };

        Assert.Equal("mjpeg", helper.GetVideoEncoder(WithCodec(state, "mjpeg"), options));
    }

    [Theory]
    [InlineData("h264", "libx264")]
    [InlineData("h265", "libx265")]
    [InlineData("hevc", "libx265")]
    [InlineData("av1", "libsvtav1")]
    [InlineData("mjpeg", "mjpeg")]
    [InlineData("vp9", "vp9")] // passthrough codec name, lowercased
    [InlineData("", "copy")]
    [InlineData(null, "copy")]
    public void GetVideoEncoder_DispatchesToSoftwareEncoderPerCodec(string? codec, string expected)
    {
        var helper = CreateHelper(out var mediaEncoder);
        mediaEncoder.Setup(m => m.SupportsEncoder(It.IsAny<string>())).Returns(false);
        var state = BuildVideoFileState();
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.none, EnableHardwareEncoding = true };

        Assert.Equal(expected, helper.GetVideoEncoder(WithCodec(state, codec), options));
    }

    private static EncodingJobInfo WithCodec(EncodingJobInfo state, string? codec)
    {
        state.OutputVideoCodec = codec;
        return state;
    }

    private static EncodingJobInfo BuildVideoFileState(VideoType videoType = VideoType.VideoFile)
    {
        var video = new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" };

        return new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            MediaSource = new MediaSourceInfo
            {
                Container = "mkv",
                MediaStreams = [video],
                VideoType = videoType,
            },
            VideoStream = video,
            VideoType = videoType,
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
