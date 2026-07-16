using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Dlna;
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
/// <remarks>
/// PR104 extended the original 4 cases with 3 more, and PR111b adds 3 mandatory HDR/10-bit cases
/// (H.264 10-bit, Dolby Vision natively supported, Dolby Vision fallback), still reusing the
/// existing Test Data infrastructure - PR98's DeviceProfile/MediaSourceInfo JSON fixtures shared
/// with Reefin.Model.Tests' StreamBuilder tests. PR111d extracted the cases, the allow-list, and
/// the loading helpers into <see cref="OracleCaseFixtures"/> so the new hot-path performance gate
/// (<c>ShadowPerformanceGateTests</c>) can reuse the exact same fixtures without forking a second
/// allow-list; this test's own behavior is unchanged. The classification is a real gate: every
/// case's divergence is asserted against <see cref="OracleCaseFixtures.ApprovedDivergences"/>, an
/// explicit, reason-carrying allow-list. A case whose <see cref="DivergenceClass"/> is
/// <see cref="DivergenceClass.PotentialRegression"/> or <see cref="DivergenceClass.Unexplained"/>
/// and is NOT in that allow-list fails the test - closing the PR101/PR103-era gap where this test
/// only classified without gating. The allow-list contains 1 root-caused entry as of PR111e: the
/// PR111b Dolby-Vision-fallback target divergence (Chrome). PR111b's H.264-10-bit case reaches
/// Equivalent via a real projector fix (LegacyDecisionProjector now treats SubtitleStreamIndex -1 as
/// no-selection), and its natively-supported Dolby Vision case reaches Equivalent DirectPlay via a
/// real v2 fix (ClientCapabilitiesMapper no longer applies MusicStreamingTranscodingBitrate as an
/// audio decode ceiling). PR111c models subtitle text-format conversion:
/// <see cref="Reefin.Playback.Decision.SubtitleTextConversion"/> now mirrors the real subtitle
/// re-encode legacy's StreamBuilder performs (<c>MediaStream.SupportsSubtitleConversionTo</c>), so
/// (Chrome, mkv-h264-ac3-srt) resolves to <see cref="DivergenceClass.Equivalent"/> and
/// (Firefox, mp4-hevc-aac-srt) to ungated <see cref="DivergenceClass.KnownV2Limitation"/> (its HDR
/// tonemap is now derived in LegacyDecisionProjector) - both WITHOUT an allow-list entry. The third
/// former srt entry (Chrome mp4) had its subtitle gap closed too, and PR111e closed the distinct,
/// pre-existing container-CSV bug it had exposed (see the input-capture reordering below and
/// <c>PlaybackEngine</c>'s CSV-aware container comparisons) - it now also resolves to Equivalent
/// WITHOUT an allow-list entry. The list must not grow without a new, equally-documented,
/// root-caused reason.
/// </remarks>
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
        var cases = OracleCaseFixtures.Cases;

        var metrics = new ShadowMetrics();
        var results = new List<(string DeviceProfile, string Source, ShadowDivergence Divergence)>();

        foreach (var (deviceProfile, source) in cases)
        {
            var options = await OracleCaseFixtures.GetMediaOptions(deviceProfile, source);

            var stopwatch = Stopwatch.StartNew();

            // PR111e: the v2 inputs are captured BEFORE legacy runs, exactly like the fixed
            // ShadowPlaybackSessionPlanner - legacy's StreamBuilder mutates the shared
            // MediaSourceInfo.Container in place (normalizing a raw ffprobe multi-value CSV down to a
            // single value), so mapping v2's inputs afterward silently fed it legacy's
            // already-degraded view of the source instead of the real one. See
            // OracleCaseFixtures.ApprovedDivergences' former Chrome/mp4-h264-ac3-aac-srt-2600k entry
            // for the divergence this ordering used to cause (now closed).
            var capabilities = DlnaPlaybackAdapter.ToCapabilities(options.Profile);
            var constraints = DlnaPlaybackAdapter.ToConstraints(options);
            var sourceSnapshots = options.MediaSources.Select(DlnaPlaybackAdapter.ToSnapshot).ToList();
            var context = DlnaPlaybackAdapter.ToContext(options.ItemId, Guid.Empty, options.MediaSourceId, MediaKind.Video, PlaybackEngine.EngineVersion);

            var legacyStreamInfo = OracleCaseFixtures.GetStreamBuilder().GetOptimalVideoStream(options);
            var plan = legacyStreamInfo is null
                ? null
                : new PlaybackPlan(legacyStreamInfo.PlayMethod, legacyStreamInfo.TranscodeReasons, legacyStreamInfo);
            var legacyVector = LegacyDecisionProjector.Project(plan);

            var decision = new PlaybackEngine().Decide(context, capabilities, sourceSnapshots, constraints);
            var v2Vector = V2DecisionProjector.Project(decision);

            var divergence = ShadowComparer.Compare(legacyVector, v2Vector);
            stopwatch.Stop();

            // PR104: feeds the same ShadowMetrics histogram the production shadow decorator uses, so
            // this test can report a real p95 for docs/major-rewrite-plan-v13.md instead of leaving
            // the "p95 observed" line unbacked by any actual measurement.
            metrics.RecordExecution(divergence.Class, stopwatch.Elapsed, budgetExceeded: false);

            Assert.True(Enum.IsDefined(divergence.Class));

            results.Add((deviceProfile, source, divergence));
        }

        var report = new StringBuilder();
        report.AppendLine("Legacy-vs-v2 shadow oracle results:");
        foreach (var result in results)
        {
            report.AppendLine(FormattableString.Invariant($"  ({result.DeviceProfile}, {result.Source}) -> {result.Divergence.Class}: {result.Divergence.Summary}"));
        }

        var snapshot = metrics.GetSnapshot();
        report.AppendLine(FormattableString.Invariant($"Shadow metrics over {snapshot.TotalExecutions} oracle comparisons: {snapshot.ToSummaryString()}"));

        _output.WriteLine(report.ToString());

        // Sanity: the simplest possible case (a fully compatible source) must not regress on the
        // core media pipeline - method, transforms, and reasons must match exactly. The transcode
        // cases are deliberately NOT constrained to Equivalent: whatever they classify as is the
        // real finding of this PR, gated below by OracleCaseFixtures.ApprovedDivergences instead.
        var (_, _, directPlayDivergence) = results.Single(r => r.DeviceProfile == "Chrome" && r.Source == "mp4-h264-aac-vtt-2600k");

        Assert.Equal(DivergenceClass.Equivalent, directPlayDivergence.Class);
        Assert.False(directPlayDivergence.MethodDiffers);
        Assert.Empty(directPlayDivergence.OnlyLegacy);
        Assert.Empty(directPlayDivergence.OnlyV2);
        Assert.Empty(directPlayDivergence.ReasonOnlyLegacy);
        Assert.Empty(directPlayDivergence.ReasonOnlyV2);

        // PR104 gate: every case's divergence must be Equivalent/ExpectedImprovement/
        // KnownV2Limitation, OR PotentialRegression/Unexplained with an explicit, written reason in
        // OracleCaseFixtures.ApprovedDivergences. Zero unapproved PotentialRegression/Unexplained
        // across the whole oracle - not just the direct-play case above - is what makes this a real
        // gate instead of a report.
        foreach (var (deviceProfile, source, divergence) in results)
        {
            if (divergence.Class is not (DivergenceClass.PotentialRegression or DivergenceClass.Unexplained))
            {
                continue;
            }

            Assert.True(
                OracleCaseFixtures.ApprovedDivergences.TryGetValue((deviceProfile, source), out var reason),
                $"({deviceProfile}, {source}) classified as {divergence.Class} ({divergence.Summary}) but is not in " +
                $"{nameof(OracleCaseFixtures.ApprovedDivergences)}. Either this is a real regression to fix, or it needs an explicit, " +
                "written entry explaining why it's acceptable - never a silent widening of the assertion.");

            _output.WriteLine(FormattableString.Invariant(
                $"  (approved divergence) ({deviceProfile}, {source}) -> {divergence.Class}: {reason}"));
        }
    }
}
