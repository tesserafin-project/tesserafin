using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.MediaEncoding;
using Reefin.Extensions.Json;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Session;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Playback;

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

    private static PlaybackSessionPlanner GetPlanner()
    {
        var mediaEncoder = new Mock<Reefin.Controller.MediaEncoding.IMediaEncoder>();
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
