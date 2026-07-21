using System.Diagnostics;
using System.Text;
using Xunit;

namespace Reefin.Playback.Contract.Scan.Tests;

/// <summary>
/// Issue #75 slice 75b: measured scan cost on a typical body, a body at the size limit, and a
/// hostile body. Not a strict pass/fail budget (that would be flaky on shared CI hardware) - it
/// records the per-scan cost so the "bounded, single-pass" claim is backed by a number, and asserts
/// only a very loose ceiling that would catch an accidental super-linear regression.
/// </summary>
public sealed class PlaybackContractScannerMeasurementTests
{
    private readonly Xunit.ITestOutputHelper _output;

    public PlaybackContractScannerMeasurementTests(Xunit.ITestOutputHelper output)
    {
        _output = output;
    }

    private static double MeasureNsPerScan(byte[] body, int iterations)
    {
        // Warm up the JIT and the model.
        for (var i = 0; i < 1000; i++)
        {
            PlaybackContractScanner.Scan(body, ContractScanTestModel.Root, body.Length, bodyLimitExceeded: false);
        }

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            PlaybackContractScanner.Scan(body, ContractScanTestModel.Root, body.Length, bodyLimitExceeded: false);
        }

        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
    }

    [Fact]
    public void Measure_Typical_AtLimit_Hostile()
    {
        var typical = Encoding.UTF8.GetBytes(
            "{\"ItemId\":\"a1b2\",\"UserId\":\"u1\",\"Capabilities\":{\"Decode\":{\"VideoCodecs\":[{\"Codec\":\"h264\",\"MaxBitrate\":8000000,\"Profiles\":[\"high\"]},{\"Codec\":\"hevc\",\"MaxBitrate\":20000000}],\"AudioCodecs\":[{\"Codec\":\"aac\",\"MaxChannels\":6}],\"SupportsHls\":true},\"OutputProfiles\":[{\"Type\":\"Video\",\"Container\":\"mp4\",\"MaxVideoBitrate\":8000000}]},\"Constraints\":{\"MaxBitrate\":10000000}}");

        var atLimit = Encoding.UTF8.GetBytes("{\"Pad\":\"" + new string('A', (256 * 1024) - 12) + "\"}");

        var sb = new StringBuilder("{");
        for (var i = 0; i < 2000; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("\"Bogus").Append(i).Append("\":\"junk_value_").Append(i).Append('"');
        }

        sb.Append('}');
        var hostile = Encoding.UTF8.GetBytes(sb.ToString());

        var typicalNs = MeasureNsPerScan(typical, 50_000);
        var atLimitNs = MeasureNsPerScan(atLimit, 5_000);
        var hostileNs = MeasureNsPerScan(hostile, 5_000);

        _output.WriteLine($"typical  body {typical.Length,7} B : {typicalNs,10:F0} ns/scan");
        _output.WriteLine($"at-limit body {atLimit.Length,7} B : {atLimitNs,10:F0} ns/scan");
        _output.WriteLine($"hostile  body {hostile.Length,7} B ({2000} unknown keys) : {hostileNs,10:F0} ns/scan");

        // Very loose ceilings - single-pass over an in-memory buffer should stay well under these on
        // any realistic CI box; they exist only to catch a gross regression, not to gate on timing.
        Assert.True(typicalNs < 500_000, $"typical scan too slow: {typicalNs:F0} ns");
        Assert.True(atLimitNs < 50_000_000, $"at-limit scan too slow: {atLimitNs:F0} ns");
    }
}
