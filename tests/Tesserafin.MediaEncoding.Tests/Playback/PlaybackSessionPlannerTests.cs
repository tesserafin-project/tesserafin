using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions.Json;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// Equivalence tests proving <see cref="PlaybackSessionPlanner"/> is a pure delegation
/// to <see cref="StreamBuilder"/> — same inputs must produce identical decisions.
/// </summary>
public class PlaybackSessionPlannerTests
{
    [Fact]
    public async Task PlanVideo_DirectPlayCase_MatchesStreamBuilder()
    {
        var options = await GetMediaOptions("Chrome", "mp4-h264-aac-vtt-2600k");

        var expected = GetStreamBuilder().GetOptimalVideoStream(options);
        var plan = GetPlanner().PlanVideo(options);

        Assert.NotNull(expected);
        Assert.NotNull(plan);
        Assert.Equal(expected.PlayMethod, plan.PlayMethod);
        Assert.Equal(PlayMethod.DirectPlay, expected.PlayMethod);
        Assert.Equal(expected.TranscodeReasons, plan.TranscodeReasons);
        Assert.NotNull(plan.StreamInfo);
        Assert.Equal(expected.Container, plan.StreamInfo.Container);
    }

    [Fact]
    public async Task PlanVideo_TranscodeCase_MatchesStreamBuilder()
    {
        var options = await GetMediaOptions("Chrome", "mp4-h264-ac3-aac-srt-2600k");

        var expected = GetStreamBuilder().GetOptimalVideoStream(options);
        var plan = GetPlanner().PlanVideo(options);

        Assert.NotNull(expected);
        Assert.NotNull(plan);
        Assert.Equal(expected.PlayMethod, plan.PlayMethod);
        Assert.Equal(PlayMethod.Transcode, expected.PlayMethod);
        Assert.Equal(expected.TranscodeReasons, plan.TranscodeReasons);
        Assert.Equal(TranscodeReason.AudioCodecNotSupported, plan.TranscodeReasons);
        Assert.NotNull(plan.StreamInfo);
        Assert.Equal(expected.Container, plan.StreamInfo.Container);
    }

    [Fact]
    public void PlanVideo_UndeclaredContainer_RemuxesThroughTheDeclaredOutput_Issue374()
    {
        var options = Options("mkv", isAvc: true, audioIsDefault: true, ["mp4"], withHlsOutput: true);

        var plan = GetPlanner().PlanVideo(options);

        Assert.NotNull(plan);
        Assert.Equal(PlayMethod.DirectStream, plan.PlayMethod);
        Assert.Equal(TranscodeReason.ContainerNotSupported, plan.TranscodeReasons);
        Assert.Equal(PlayMethod.Transcode, plan.StreamInfo!.PlayMethod);
        Assert.Equal(MediaStreamProtocol.hls, plan.StreamInfo.SubProtocol);
        Assert.Equal("ts", plan.StreamInfo.Container);
        Assert.DoesNotContain("Static=true", plan.StreamInfo.ToUrl(string.Empty, "t", null), StringComparison.OrdinalIgnoreCase);
        Assert.True(options.EnableDirectStream, "The stored request must keep what the client asked for.");
    }

    [Fact]
    public void PlanVideo_AnnexBH264InAvi_ReportsTheVideoReencodeExecutionPerforms_Issue374()
    {
        var plan = GetPlanner().PlanVideo(Options("avi", isAvc: false, audioIsDefault: false, ["mp4"], withHlsOutput: true));

        Assert.NotNull(plan);
        Assert.Equal(PlayMethod.Transcode, plan.PlayMethod);
        Assert.Equal(TranscodeReason.ContainerNotSupported | TranscodeReason.VideoCodecTagNotSupported, plan.TranscodeReasons);
    }

    [Fact]
    public void PlanVideo_AnnexBH264InAvi_TranscodingForbidden_IsRefused_Issue374()
    {
        var options = Options("avi", isAvc: false, audioIsDefault: false, ["mp4"], withHlsOutput: true);
        options.MediaSources[0].SupportsTranscoding = false;

        Assert.Null(GetPlanner().PlanVideo(options));
    }

    [Fact]
    public void PlanVideo_NoOutputProfile_CopiesIntoDeclaredMp4_Issue374()
    {
        var options = Options("mkv", isAvc: true, audioIsDefault: true, ["mp4"], withHlsOutput: false);

        var plan = GetPlanner().PlanVideo(options);

        Assert.NotNull(plan);
        Assert.Equal(PlayMethod.DirectStream, plan.PlayMethod);
        Assert.Equal(MediaStreamProtocol.http, plan.StreamInfo!.SubProtocol);
        Assert.Equal("mp4", plan.StreamInfo.Container);
        Assert.Empty(options.Profile.TranscodingProfiles);
    }

    [Fact]
    public void PlanVideo_NoOutputProfile_NeverReencodesOnTheStrengthOfADirectPlayDeclaration_Issue374()
    {
        Assert.Null(GetPlanner().PlanVideo(Options("avi", isAvc: false, audioIsDefault: false, ["mp4"], withHlsOutput: false)));
    }

    [Fact]
    public void PlanVideo_NothingDeclaredIsProducible_IsRefused_Issue374()
    {
        Assert.Null(GetPlanner().PlanVideo(Options("mkv", isAvc: true, audioIsDefault: true, ["webm"], withHlsOutput: false)));
    }

    [Fact]
    public void PlanVideo_DeclaredContainerWithDirectPlayOff_KeepsTheSourceFile_Issue374()
    {
        var options = Options("mkv", isAvc: true, audioIsDefault: true, ["mkv"], withHlsOutput: true);
        options.EnableDirectPlay = false;

        var plan = GetPlanner().PlanVideo(options);

        Assert.NotNull(plan);
        Assert.Equal(PlayMethod.DirectStream, plan.StreamInfo!.PlayMethod);
        Assert.Equal((TranscodeReason)0, plan.TranscodeReasons);
    }

    [Fact]
    public void PlanVideo_NoDirectDeliveryAndNoOutput_IsRefusedNotAHollowTranscode_Issue374()
    {
        var options = Options("mkv", isAvc: true, audioIsDefault: true, ["webm"], withHlsOutput: false);
        options.EnableDirectStream = false;

        Assert.Null(GetPlanner().PlanVideo(options));
    }

    [Fact]
    public void PlanVideo_WhenAnotherEngineDecides_ALegacyRefusalDoesNotRefuseTheSession_Issue374()
    {
        var options = Options("mkv", isAvc: true, audioIsDefault: true, ["webm"], withHlsOutput: false);
        options.LegacyPlanIsFallbackOnly = true;

        Assert.NotNull(GetPlanner().PlanVideo(options));
    }

    [Fact]
    public void PlanVideo_PlaybackInfoCaller_KeepsThePlanItsFlagsAreReadFrom_Issue374()
    {
        var options = Options("mkv", isAvc: true, audioIsDefault: true, ["webm"], withHlsOutput: false);
        options.RequireDeclaredDelivery = false;
        options.EnableDirectStream = false;

        Assert.NotNull(GetPlanner().PlanVideo(options));
    }

    [Fact]
    public void PlanVideo_LaterMp4Declaration_IsTheOneThatVouchesForTheCopy_Issue374()
    {
        var options = Options("mkv", isAvc: true, audioIsDefault: true, ["mp4"], withHlsOutput: false);
        options.Profile.DirectPlayProfiles =
        [
            new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mp4", VideoCodec = "hevc", AudioCodec = "aac" },
            .. options.Profile.DirectPlayProfiles,
        ];

        var plan = GetPlanner().PlanVideo(options);

        Assert.NotNull(plan);
        Assert.Equal(PlayMethod.DirectStream, plan.PlayMethod);
    }

    [Theory]
    [InlineData(SubtitleDeliveryMethod.External, PlayMethod.DirectStream)]
    [InlineData(SubtitleDeliveryMethod.Encode, PlayMethod.Transcode)]
    public void PlanVideo_SelectedSubtitle_OnlyBurningInStopsACopy_Issue374(SubtitleDeliveryMethod delivery, PlayMethod expected)
    {
        var options = Options("mkv", isAvc: true, audioIsDefault: true, ["mp4"], withHlsOutput: true);
        var source = options.MediaSources[0];
        source.MediaStreams = [.. source.MediaStreams, new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = true, Path = "/media/source.srt", SupportsExternalStream = true }];
        options.SubtitleStreamIndex = 2;
        options.MediaSourceId = source.Id;
        options.Profile.SubtitleProfiles = [new SubtitleProfile { Format = "srt", Method = delivery }];

        var plan = GetPlanner().PlanVideo(options);

        Assert.NotNull(plan);
        Assert.Equal(expected, plan.PlayMethod);
    }

    private static MediaOptions Options(string container, bool isAvc, bool audioIsDefault, string[] directPlayContainers, bool withHlsOutput)
    {
        var source = new MediaSourceInfo
        {
            Id = "0123456789abcdef0123456789abcdef",
            Path = "/media/source." + container,
            Container = container,
            Bitrate = 1_000_000,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            DefaultAudioStreamIndex = 1,
            DefaultAudioIndexSource = AudioIndexSource.Default,
            MediaStreams =
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", IsAVC = isAvc, Width = 640, Height = 360 },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = audioIsDefault },
            ],
        };

        return new MediaOptions
        {
            ItemId = Guid.NewGuid(),
            MediaSources = [source],
            DeviceId = "device",
            RequireDeclaredDelivery = true,
            AllowVideoStreamCopy = true,
            AllowAudioStreamCopy = true,
            Profile = new DeviceProfile
            {
                DirectPlayProfiles = [new DirectPlayProfile { Type = DlnaProfileType.Video, Container = string.Join(',', directPlayContainers), VideoCodec = "h264", AudioCodec = "aac" }],
                TranscodingProfiles = withHlsOutput
                    ? [new TranscodingProfile { Type = DlnaProfileType.Video, Protocol = MediaStreamProtocol.hls, Container = "ts", VideoCodec = "h264", AudioCodec = "aac", Context = EncodingContext.Streaming }]
                    : [],
            },
        };
    }

    private static PlaybackSessionPlanner GetPlanner()
    {
        var mediaEncoder = new Mock<Tesserafin.Controller.MediaEncoding.IMediaEncoder>();
        var logger = NullLogger<PlaybackSessionPlanner>.Instance;
        return new PlaybackSessionPlanner(mediaEncoder.Object, logger);
    }

    private static StreamBuilder GetStreamBuilder()
    {
        var transcodeSupport = new Mock<ITranscoderSupport>();
        var logger = NullLogger<PlaybackSessionPlannerTests>.Instance;
        return new StreamBuilder(transcodeSupport.Object, logger);
    }

    private static async ValueTask<MediaOptions> GetMediaOptions(string deviceProfile, params string[] sources)
    {
        var mediaSources = sources.Select(src => TestData<MediaSourceInfo>(src))
            .Select(val => val.Result)
            .ToArray();
        var mediaSourceId = mediaSources[0]?.Id;

        var dp = await TestData<DeviceProfile>(deviceProfile);

        return new MediaOptions()
        {
            ItemId = new Guid("11D229B7-2D48-4B95-9F9B-49F6AB75E613"),
            MediaSourceId = mediaSourceId,
            MediaSources = mediaSources,
            DeviceId = "test-deviceId",
            Profile = dp,
            AllowAudioStreamCopy = true,
            AllowVideoStreamCopy = true,
            EnableDirectStream = false,
        };
    }

    private static async ValueTask<T> TestData<T>(string name)
    {
        var path = Path.Join("Test Data", typeof(T).Name + "-" + name + ".json");

        using var stream = File.OpenRead(path);

        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options);
        if (value is not null)
        {
            return value;
        }

        throw new SerializationException("Invalid test data: " + name);
    }
}
