using System.Threading.Tasks;
using Tesserafin.Model.Dlna;
using Tesserafin.Playback.Dlna;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// PR112b's real gate: for each of the 9 mandatory <see cref="OracleCaseFixtures.Cases"/>, maps the
/// case's legacy <see cref="DeviceProfile"/>/<see cref="MediaOptions"/> forward into domain
/// <c>ClientCapabilities</c>/<c>PlaybackConstraints</c> (the same <see cref="DlnaPlaybackAdapter"/>
/// calls the v2 client contract now accepts as its request shape), then back into a reconstructed
/// legacy <see cref="DeviceProfile"/>/<see cref="MediaOptions"/> via <see cref="ReverseDlnaAdapter"/>
/// (the temporary v2-to-DLNA adapter <c>PlaybackSessionsController</c> now uses), and asserts the
/// real legacy <c>StreamBuilder</c> produces the SAME plan for the reconstructed options as for the
/// original. This is what proves the new client contract (<see cref="ClientCapabilities"/> in, not
/// <see cref="DeviceProfile"/>) does not change a single live decision while legacy remains the
/// source of truth (PR112b, until the v2 execution layer lands - PR114a).
/// </summary>
public sealed class ReverseAdapterRoundTripTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (deviceProfile, source) in OracleCaseFixtures.Cases)
        {
            data.Add(deviceProfile, source);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RoundTrip_ProducesSameLegacyPlan(string deviceProfile, string source)
    {
        // Two independent loads of the same fixtures: StreamBuilder mutates MediaSourceInfo in
        // place (see OracleParityTests' remarks on NormalizeMediaSourceFormatIntoSingleContainer),
        // so baseline and round-tripped runs must never share a MediaSourceInfo instance.
        var baselineOptions = await OracleCaseFixtures.GetMediaOptions(deviceProfile, source);

        // Capture the v2-facing inputs BEFORE running legacy on baselineOptions, same ordering
        // rationale as OracleParityTests/PR111e.
        var capabilities = DlnaPlaybackAdapter.ToCapabilities(baselineOptions.Profile);
        var constraints = DlnaPlaybackAdapter.ToConstraints(baselineOptions);

        var baselineStream = OracleCaseFixtures.GetStreamBuilder().GetOptimalVideoStream(baselineOptions);

        var roundTrippedOptions = await OracleCaseFixtures.GetMediaOptions(deviceProfile, source);
        roundTrippedOptions.Profile = ReverseDlnaAdapter.ToDeviceProfile(capabilities);
        ReverseDlnaAdapter.ApplyConstraints(roundTrippedOptions, constraints);

        var roundTrippedStream = OracleCaseFixtures.GetStreamBuilder().GetOptimalVideoStream(roundTrippedOptions);

        Assert.NotNull(baselineStream);
        Assert.NotNull(roundTrippedStream);

        Assert.Equal(baselineStream!.PlayMethod, roundTrippedStream!.PlayMethod);
        Assert.Equal(baselineStream.TranscodeReasons, roundTrippedStream.TranscodeReasons);
        Assert.Equal(baselineStream.Container, roundTrippedStream.Container);
        Assert.Equal(baselineStream.SubProtocol, roundTrippedStream.SubProtocol);
        Assert.Equal(baselineStream.TargetVideoCodec, roundTrippedStream.TargetVideoCodec);
        Assert.Equal(baselineStream.TargetAudioCodec, roundTrippedStream.TargetAudioCodec);
        Assert.Equal(baselineStream.VideoBitrate, roundTrippedStream.VideoBitrate);
        Assert.Equal(baselineStream.AudioBitrate, roundTrippedStream.AudioBitrate);
        Assert.Equal(baselineStream.MaxWidth, roundTrippedStream.MaxWidth);
        Assert.Equal(baselineStream.MaxHeight, roundTrippedStream.MaxHeight);
        Assert.Equal(baselineStream.AudioStreamIndex, roundTrippedStream.AudioStreamIndex);
        Assert.Equal(baselineStream.SubtitleStreamIndex, roundTrippedStream.SubtitleStreamIndex);
        Assert.Equal(baselineStream.SubtitleDeliveryMethod, roundTrippedStream.SubtitleDeliveryMethod);
    }
}
