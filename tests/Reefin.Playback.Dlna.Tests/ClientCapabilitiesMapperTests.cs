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

        Assert.Contains("mp4", capabilities.Containers);
        Assert.Contains("mkv", capabilities.Containers);
    }

    [Fact]
    public static void ToCapabilities_ProjectsH264VideoCodecCapability()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var h264 = Assert.Single(capabilities.VideoCodecs, c => c.Codec == "h264");

        Assert.Equal(["high", "main"], h264.Profiles.OrderBy(p => p, System.StringComparer.Ordinal));
        Assert.Equal(51, h264.MaxLevel);
        Assert.Equal(8, h264.MaxBitDepth);
    }

    [Fact]
    public static void ToCapabilities_ProjectsMaxResolutionFromWidthHeightConditions()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.Equal(new Resolution(1920, 1080), capabilities.MaxResolution);
    }

    [Fact]
    public static void ToCapabilities_ProjectsAacAudioCodecCapability()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var aac = Assert.Single(capabilities.AudioCodecs, c => c.Codec == "aac");

        Assert.Equal(2, aac.MaxChannels);
    }

    [Fact]
    public static void ToCapabilities_MapsSubtitleDeliveryMethodsAndDropsDropMethod()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.Contains(capabilities.SubtitleDelivery, s => s.Format == "srt" && s.Method == SubtitleDeliveryMethod.External);
        Assert.Contains(capabilities.SubtitleDelivery, s => s.Format == "ass" && s.Method == SubtitleDeliveryMethod.Burn);
        Assert.Equal(2, capabilities.SubtitleDelivery.Count);
    }

    [Fact]
    public static void ToCapabilities_ProjectsSupportsHlsFromTranscodingProfiles()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.True(capabilities.SupportsHls);
    }

    [Fact]
    public static void ToCapabilities_ProjectsSupportsDashAsFalse()
    {
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.False(capabilities.SupportsDash);
    }
}
