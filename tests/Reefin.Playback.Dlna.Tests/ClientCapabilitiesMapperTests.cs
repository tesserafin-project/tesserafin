using System.Linq;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Dlna.Tests;

/// <summary>
/// Tests for <see cref="ClientCapabilitiesMapper"/>: verifies that a realistic web-client
/// <c>DeviceProfile</c> projects into the expected declared <see cref="ClientCapabilities"/>.
/// </summary>
public static class ClientCapabilitiesMapperTests
{
    [Fact]
    public static void ToCapabilities_ProjectsContainers()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.Contains("mp4", capabilities.Decode.Containers);
        Assert.Contains("mkv", capabilities.Decode.Containers);
    }

    [Fact]
    public static void ToCapabilities_ProjectsH264VideoCodecCapability()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var h264 = Assert.Single(capabilities.Decode.VideoCodecs, c => c.Codec == "h264");

        Assert.Equal(["high", "main"], h264.Profiles.OrderBy(p => p, System.StringComparer.Ordinal));
        Assert.Equal(51, h264.MaxLevel);
        Assert.Equal(8, h264.MaxBitDepth);
    }

    [Fact]
    public static void ToCapabilities_ProjectsMaxResolutionFromWidthHeightConditions()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.Equal(new Resolution(1920, 1080), capabilities.Decode.MaxResolution);
    }

    [Fact]
    public static void ToCapabilities_ProjectsAacAudioCodecCapability()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var aac = Assert.Single(capabilities.Decode.AudioCodecs, c => c.Codec == "aac");

        Assert.Equal(2, aac.MaxChannels);
    }

    [Fact]
    public static void ToCapabilities_MapsSubtitleDeliveryMethodsAndDropsDropMethod()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.Contains(capabilities.Decode.SubtitleDelivery, s => s.Format == "srt" && s.Method == SubtitleDeliveryMethod.External);
        Assert.Contains(capabilities.Decode.SubtitleDelivery, s => s.Format == "ass" && s.Method == SubtitleDeliveryMethod.Burn);
        Assert.Equal(2, capabilities.Decode.SubtitleDelivery.Count);
    }

    [Fact]
    public static void ToCapabilities_ProjectsSupportsHlsFromTranscodingProfiles()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.True(capabilities.Decode.SupportsHls);
    }

    [Fact]
    public static void ToCapabilities_ProjectsSupportsDashAsFalse()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.False(capabilities.Decode.SupportsDash);
    }

    [Fact]
    public static void ToCapabilities_ProjectsOutputProfilesInTranscodingProfileOrder()
    {
        // RFC PR102 / PR98 oracle finding: TranscodingProfile order is the client's transcoding
        // preference order and must survive into PlaybackOutputProfile unchanged. The fixture
        // declares mp4 (av1,h264,vp9) before ts (h264): the mapped output must preserve that order.
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var videoProfiles = capabilities.OutputProfiles.Where(p => p.Type == MediaKind.Video).ToList();

        Assert.Equal(2, videoProfiles.Count);
        Assert.Equal("mp4", videoProfiles[0].Container);
        Assert.Equal(["av1", "h264", "vp9"], videoProfiles[0].VideoCodecs);
        Assert.Equal("ts", videoProfiles[1].Container);
        Assert.Equal(["h264"], videoProfiles[1].VideoCodecs);
    }

    [Fact]
    public static void ToCapabilities_ProjectsOutputProfileAudioCodecsAndLimits()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var mp4Profile = capabilities.OutputProfiles.Single(p => p.Type == MediaKind.Video && p.Container == "mp4");

        Assert.Equal(["aac", "ac3"], mp4Profile.AudioCodecs);
        Assert.Equal(StreamingProtocol.Hls, mp4Profile.Protocol);
        Assert.Equal(6, mp4Profile.MaxAudioChannels);
    }

    [Fact]
    public static void ToCapabilities_ExcludesStaticContextTranscodingProfiles()
    {
        // The v2 domain models streaming playback only; a device's Static-context (whole-file
        // conversion) TranscodingProfiles have no PlaybackOutputProfile equivalent and must not
        // pollute the streaming preference order the engine reads.
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.DoesNotContain(capabilities.OutputProfiles, p => p.Type == MediaKind.Audio);
    }
}
