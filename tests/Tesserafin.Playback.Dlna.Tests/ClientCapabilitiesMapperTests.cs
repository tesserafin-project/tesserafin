using System.Linq;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Dlna.Tests;

/// <summary>
/// Tests for <see cref="ClientCapabilitiesMapper"/>: verifies that a realistic web-client
/// <c>DeviceProfile</c> projects into the expected declared <see cref="ClientCapabilities"/>.
/// </summary>
public static class ClientCapabilitiesMapperTests
{
    [Fact]
    public static void ToCapabilities_ProjectsDirectPlayProfilesNotFlattened()
    {
        // RFC PR102b problem #1: the fixture declares a Video DirectPlayProfile (mp4/mkv/webm,
        // h264/hevc) and a separate Audio DirectPlayProfile (mp4/mkv/webm, aac/ac3/mp3) - each must
        // survive as its own DecodeProfile, not be collapsed into a flat container list.
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        Assert.Equal(2, capabilities.Decode.DirectPlayProfiles.Count);

        var videoProfile = Assert.Single(capabilities.Decode.DirectPlayProfiles, p => p.Type == MediaKind.Video);
        Assert.Contains("mp4", videoProfile.Containers);
        Assert.Contains("mkv", videoProfile.Containers);
        Assert.Contains("webm", videoProfile.Containers);
        Assert.Equal(["h264", "hevc"], videoProfile.VideoCodecs);
        Assert.Empty(videoProfile.AudioCodecs);

        var audioProfile = Assert.Single(capabilities.Decode.DirectPlayProfiles, p => p.Type == MediaKind.Audio);
        Assert.Contains("mp4", audioProfile.Containers);
        Assert.Empty(audioProfile.VideoCodecs);
        Assert.Equal(["aac", "ac3", "mp3"], audioProfile.AudioCodecs);
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

        var h264 = Assert.Single(capabilities.Decode.VideoCodecs, c => c.Codec == "h264");
        Assert.Equal(new Resolution(1920, 1080), h264.MaxResolution);
    }

    [Fact]
    public static void ToCapabilities_CodecWithNoOwnCodecProfile_HasNoResolutionLimit()
    {
        // RFC PR102b problem #2: the fixture's h264 CodecProfile declares Width<=1920/Height<=1080,
        // but hevc (declared only on the DirectPlayProfile's VideoCodec list, no CodecProfile of its
        // own) must not inherit that limit - a per-codec model must not let one codec's limit leak
        // onto another the way the old global minimum did.
        var capabilities = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var hevc = Assert.Single(capabilities.Decode.VideoCodecs, c => c.Codec == "hevc");
        Assert.Null(hevc.MaxResolution);
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
