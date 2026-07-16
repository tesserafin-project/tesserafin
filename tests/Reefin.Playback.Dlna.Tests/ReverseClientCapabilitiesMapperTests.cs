using System.Linq;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Dlna.Tests;

/// <summary>
/// Tests for <see cref="ReverseClientCapabilitiesMapper"/>: verifies that mapping domain
/// <see cref="ClientCapabilities"/> back into a legacy <c>DeviceProfile</c> and forward again through
/// <see cref="ClientCapabilitiesMapper"/> reproduces the same capabilities (a fixed point of the
/// forward/reverse pair) - the property the real oracle-fixture round trip
/// (<c>Reefin.Playback.Shadow.Tests.ReverseAdapterRoundTripTests</c>) additionally proves holds for
/// the real legacy <c>StreamBuilder</c>, not just for this projection.
/// </summary>
public static class ReverseClientCapabilitiesMapperTests
{
    private static ClientCapabilities RoundTrip(ClientCapabilities capabilities) =>
        ClientCapabilitiesMapper.ToCapabilities(ReverseClientCapabilitiesMapper.ToDeviceProfile(capabilities));

    [Fact]
    public static void RoundTrip_PreservesDirectPlayProfilesNotFlattened()
    {
        var original = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var roundTripped = RoundTrip(original);

        Assert.Equal(original.Decode.DirectPlayProfiles.Count, roundTripped.Decode.DirectPlayProfiles.Count);
        foreach (var profile in original.Decode.DirectPlayProfiles)
        {
            Assert.Contains(
                roundTripped.Decode.DirectPlayProfiles,
                p => p.Type == profile.Type
                    && p.Containers.OrderBy(c => c).SequenceEqual(profile.Containers.OrderBy(c => c))
                    && p.VideoCodecs.SequenceEqual(profile.VideoCodecs)
                    && p.AudioCodecs.SequenceEqual(profile.AudioCodecs));
        }
    }

    [Fact]
    public static void RoundTrip_PreservesPerCodecVideoLimits()
    {
        var original = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var roundTripped = RoundTrip(original);

        var originalH264 = original.Decode.VideoCodecs.Single(c => c.Codec == "h264");
        var roundTrippedH264 = roundTripped.Decode.VideoCodecs.Single(c => c.Codec == "h264");

        Assert.Equal(originalH264.Profiles.OrderBy(p => p), roundTrippedH264.Profiles.OrderBy(p => p));
        Assert.Equal(originalH264.MaxLevel, roundTrippedH264.MaxLevel);
        Assert.Equal(originalH264.MaxBitDepth, roundTrippedH264.MaxBitDepth);
        Assert.Equal(originalH264.MaxResolution, roundTrippedH264.MaxResolution);
        Assert.Equal(originalH264.MaxBitrate, roundTrippedH264.MaxBitrate);
    }

    [Fact]
    public static void RoundTrip_PreservesAudioCodecLimits()
    {
        var original = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var roundTripped = RoundTrip(original);

        var originalAac = original.Decode.AudioCodecs.Single(c => c.Codec == "aac");
        var roundTrippedAac = roundTripped.Decode.AudioCodecs.Single(c => c.Codec == "aac");

        Assert.Equal(originalAac.MaxChannels, roundTrippedAac.MaxChannels);
        Assert.Equal(originalAac.MaxSampleRate, roundTrippedAac.MaxSampleRate);
        Assert.Equal(originalAac.MaxBitDepth, roundTrippedAac.MaxBitDepth);
        Assert.Equal(originalAac.MaxBitrate, roundTrippedAac.MaxBitrate);
    }

    [Fact]
    public static void RoundTrip_PreservesSubtitleDeliveryExceptDrop()
    {
        // Encode/Embed/External/Hls all have a domain equivalent and must survive; the fixture
        // deliberately has no Drop entry, so this only proves the 4 that CAN round-trip do.
        var original = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var roundTripped = RoundTrip(original);

        Assert.Equal(
            original.Decode.SubtitleDelivery.OrderBy(s => s.Format),
            roundTripped.Decode.SubtitleDelivery.OrderBy(s => s.Format));
    }

    [Fact]
    public static void RoundTrip_PreservesOutputProfileOrderAndLimits()
    {
        var original = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var roundTripped = RoundTrip(original);

        Assert.Equal(original.OutputProfiles.Count, roundTripped.OutputProfiles.Count);
        for (var i = 0; i < original.OutputProfiles.Count; i++)
        {
            var originalProfile = original.OutputProfiles[i];
            var roundTrippedProfile = roundTripped.OutputProfiles[i];

            Assert.Equal(originalProfile.Type, roundTrippedProfile.Type);
            Assert.Equal(originalProfile.Protocol, roundTrippedProfile.Protocol);
            Assert.Equal(originalProfile.Container, roundTrippedProfile.Container);
            Assert.Equal(originalProfile.VideoCodecs, roundTrippedProfile.VideoCodecs);
            Assert.Equal(originalProfile.AudioCodecs, roundTrippedProfile.AudioCodecs);
            Assert.Equal(originalProfile.MaxVideoBitrate, roundTrippedProfile.MaxVideoBitrate);
            Assert.Equal(originalProfile.MaxAudioBitrate, roundTrippedProfile.MaxAudioBitrate);
            Assert.Equal(originalProfile.MaxAudioChannels, roundTrippedProfile.MaxAudioChannels);
        }
    }

    [Fact]
    public static void RoundTrip_PreservesSupportsHls()
    {
        var original = ClientCapabilitiesMapper.ToCapabilities(DeviceProfileFixture.BuildWebClientProfile());

        var roundTripped = RoundTrip(original);

        Assert.Equal(original.Decode.SupportsHls, roundTripped.Decode.SupportsHls);
    }
}
