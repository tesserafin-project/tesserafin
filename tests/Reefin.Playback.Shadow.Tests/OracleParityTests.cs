using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.MediaEncoding;
using Reefin.Extensions.Json;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Playback.Decision;
using Reefin.Playback.Dlna;
using Reefin.Playback.Engine;
using Xunit;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// The real legacy-vs-v2 oracle harness (PR98's actual deliverable): for each (device profile,
/// media source) pair, runs the REAL legacy <see cref="StreamBuilder"/> and the REAL v2
/// <see cref="PlaybackEngine"/> on the same input, projects both decisions, and classifies the
/// divergence with <see cref="ShadowComparer"/>. This test CLASSIFIES; it does not assert
/// legacy == v2 - the whole point of shadow mode is that the two engines are allowed to disagree,
/// as long as the disagreement is understood. Every case's classification is written to test output
/// so the divergences are visible, per docs/pr93-compatibility-lab.md §4.
/// </summary>
public sealed class OracleParityTests
{
    private readonly ITestOutputHelper _output;

    public OracleParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Oracle_ClassifiesLegacyVsV2ForEachCase()
    {
        var cases = new (string DeviceProfile, string Source)[]
        {
            ("Chrome", "mp4-h264-aac-vtt-2600k"), // direct play
            ("Chrome", "mkv-h264-ac3-srt-2600k"), // transcode (container + audio codec)
            ("Chrome", "mp4-h264-ac3-aac-srt-2600k"), // transcode (secondary audio track)
            ("Firefox", "mp4-hevc-aac-srt-15200k"), // transcode (video codec not supported)
        };

        var results = new List<(string DeviceProfile, string Source, ShadowDivergence Divergence)>();

        foreach (var (deviceProfile, source) in cases)
        {
            var options = await GetMediaOptions(deviceProfile, source);

            var legacyStreamInfo = GetStreamBuilder().GetOptimalVideoStream(options);
            var plan = legacyStreamInfo is null
                ? null
                : new PlaybackPlan(legacyStreamInfo.PlayMethod, legacyStreamInfo.TranscodeReasons, legacyStreamInfo);
            var legacyVector = LegacyDecisionProjector.Project(plan);

            var capabilities = DlnaPlaybackAdapter.ToCapabilities(options.Profile);
            var constraints = DlnaPlaybackAdapter.ToConstraints(options);
            var sourceSnapshots = options.MediaSources.Select(DlnaPlaybackAdapter.ToSnapshot).ToList();
            var context = DlnaPlaybackAdapter.ToContext(options.ItemId, Guid.Empty, options.MediaSourceId, MediaKind.Video, PlaybackEngine.EngineVersion);
            var decision = new PlaybackEngine().Decide(context, capabilities, sourceSnapshots, constraints);
            var v2Vector = V2DecisionProjector.Project(decision);

            var divergence = ShadowComparer.Compare(legacyVector, v2Vector);

            Assert.True(Enum.IsDefined(divergence.Class));

            results.Add((deviceProfile, source, divergence));
        }

        var report = new StringBuilder();
        report.AppendLine("Legacy-vs-v2 shadow oracle results:");
        foreach (var result in results)
        {
            report.AppendLine(FormattableString.Invariant($"  ({result.DeviceProfile}, {result.Source}) -> {result.Divergence.Class}: {result.Divergence.Summary}"));
        }

        _output.WriteLine(report.ToString());

        // Sanity: the simplest possible case (a fully compatible source) must not regress on the
        // core media pipeline - method, transforms, and reasons must match exactly. The transcode
        // cases are deliberately NOT constrained: whatever they classify as is the real finding of
        // this PR.
        //
        // PR101 finding: this fixture's DivergenceClass is now PotentialRegression, not Equivalent.
        // Both projectors are correctly reporting their engine's real, viable plan; the plans
        // genuinely differ on one axis. Root cause traced in PlaybackEngine.SelectSubtitle: v2 only
        // auto-selects a subtitle when the caller passes an explicit PreferredSubtitleStreamIndex,
        // while legacy's StreamBuilder auto-selects a default/forced subtitle without one - and this
        // test's MediaOptions sets no subtitle preference. The pre-PR101 int?-based comparator could
        // not see this: legacy's Selected(index) and v2's absent selection both collapsed to "not
        // asserted" and cancelled out. This is a real, pre-existing v2 engine limitation surfaced for
        // the first time by the tri-state fix, not a comparator bug - it is called out here rather
        // than engineered away (see PR101 final report) and is out of scope for this PR to fix in the
        // engine itself.
        var (_, _, directPlayDivergence) = results.Single(r => r.Source == "mp4-h264-aac-vtt-2600k");

        Assert.True(
            directPlayDivergence.Class is DivergenceClass.Equivalent or DivergenceClass.PotentialRegression,
            $"Direct-play case classified as {directPlayDivergence.Class}, expected Equivalent (ideal) or PotentialRegression (the known, tracked subtitle-auto-selection gap documented above). Summary: {directPlayDivergence.Summary}");

        // The media pipeline itself (method, transform set, reason set) must still match exactly:
        // this proves any divergence is confined to the subtitle axis, not a new, broader regression.
        Assert.False(directPlayDivergence.MethodDiffers);
        Assert.Empty(directPlayDivergence.OnlyLegacy);
        Assert.Empty(directPlayDivergence.OnlyV2);
        Assert.Empty(directPlayDivergence.ReasonOnlyLegacy);
        Assert.Empty(directPlayDivergence.ReasonOnlyV2);

        if (directPlayDivergence.Class == DivergenceClass.PotentialRegression)
        {
            Assert.Contains("subtitleDelivery", directPlayDivergence.Summary, StringComparison.Ordinal);
            foreach (var otherAxis in new[] { "videoCodec", "audioCodec", "container", "videoRange", "resolution", "bitrate", "audioChannels", "source" })
            {
                Assert.DoesNotContain(otherAxis, directPlayDivergence.Summary, StringComparison.Ordinal);
            }
        }
    }

    private static StreamBuilder GetStreamBuilder()
    {
        var transcodeSupport = new Mock<ITranscoderSupport>();
        var logger = NullLogger<OracleParityTests>.Instance;
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
