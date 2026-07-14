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
        // PR101 finding (closed by PR103): this fixture's DivergenceClass was PotentialRegression,
        // not Equivalent. Root cause traced in PlaybackEngine.SelectSubtitle: v2 only auto-selected a
        // subtitle when the caller passed an explicit PreferredSubtitleStreamIndex, while legacy's
        // StreamBuilder auto-selects a default/forced subtitle without one - and this test's
        // MediaOptions sets no subtitle preference.
        //
        // PR103: PlaybackEngine now reproduces MediaStreamSelector.GetDefaultSubtitleStreamIndex's
        // default/forced auto-selection (SelectDefaultSubtitle), so v2 also auto-selects this
        // source's one subtitle stream (webvtt, IsExternal=true) under SubtitlePlaybackMode.Default.
        // Closing the selection gap exposed a second, narrower one: the stream's probed codec name
        // ("webvtt") didn't match the client's declared SubtitleProfile.Format ("vtt") under v2's
        // strict case-insensitive equality, where legacy's StreamBuilder.GetExternalSubtitleProfile
        // (StreamBuilder.cs:1594-1624) resolves the two as the same format via
        // MediaStream.SupportsSubtitleConversionTo. MediaSourceSnapshotMapper.NormalizeSubtitleFormat
        // now normalizes this one identity alias (same format, different spelling) - not a general
        // text-subtitle-format-conversion model, which stays unbuilt (see below) - so this fixture's
        // divergence closes for real: v2 also resolves External delivery and direct-plays.
        var (_, _, directPlayDivergence) = results.Single(r => r.Source == "mp4-h264-aac-vtt-2600k");

        Assert.Equal(DivergenceClass.Equivalent, directPlayDivergence.Class);
        Assert.False(directPlayDivergence.MethodDiffers);
        Assert.Empty(directPlayDivergence.OnlyLegacy);
        Assert.Empty(directPlayDivergence.OnlyV2);
        Assert.Empty(directPlayDivergence.ReasonOnlyLegacy);
        Assert.Empty(directPlayDivergence.ReasonOnlyV2);

        // The three transcode cases all involve srt->vtt or genuinely-incompatible-format subtitle
        // conversion (real re-encoding, not a spelling alias): PlaybackEngine has no text-subtitle-
        // format-conversion model (only exact-format capability matching, same as pre-PR103), so it
        // burns the subtitle in instead of delivering it externally like legacy does. This is a real,
        // documented, out-of-scope-for-PR103 gap (RFC-worthy, like the per-codec/per-profile capability
        // splits PR102/PR102b needed their own PRs for) - not silently accepted by loosening an
        // assertion, just not asserted on since this test classifies rather than gates.
        foreach (var (deviceProfile, source, divergence) in results.Where(r => r.Source != "mp4-h264-aac-vtt-2600k"))
        {
            _output.WriteLine(FormattableString.Invariant(
                $"  (unasserted, documented gap) ({deviceProfile}, {source}) -> {divergence.Class}: subtitle text-format conversion (srt/hevc source -> vtt) not modeled by v2, see PlaybackEngine remarks."));
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
