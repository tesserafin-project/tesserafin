using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// <remarks>
/// PR104 extended the original 4 cases with 3 more, and PR111b adds 3 mandatory HDR/10-bit cases
/// (H.264 10-bit, Dolby Vision natively supported, Dolby Vision fallback), still reusing the
/// existing Test Data infrastructure - PR98's DeviceProfile/MediaSourceInfo JSON fixtures shared
/// with Reefin.Model.Tests' StreamBuilder tests. The classification is a real gate: every case's
/// divergence is asserted against <see cref="ApprovedDivergences"/>, an explicit, reason-carrying
/// allow-list. A case whose <see cref="DivergenceClass"/> is
/// <see cref="DivergenceClass.PotentialRegression"/> or <see cref="DivergenceClass.Unexplained"/>
/// and is NOT in that allow-list fails the test - closing the PR101/PR103-era gap where this test
/// only classified without gating. The allow-list contains 2 root-caused entries: the PR111b
/// Dolby-Vision-fallback target divergence (Chrome), and a PR111c-exposed container-normalization
/// bug (Chrome mp4 srt case, see its entry). PR111b's H.264-10-bit case reaches Equivalent via a
/// real projector fix (LegacyDecisionProjector now treats SubtitleStreamIndex -1 as no-selection),
/// and its natively-supported Dolby Vision case reaches Equivalent DirectPlay via a real v2 fix
/// (ClientCapabilitiesMapper no longer applies MusicStreamingTranscodingBitrate as an audio decode
/// ceiling). PR111c models subtitle text-format conversion:
/// <see cref="Reefin.Playback.Decision.SubtitleTextConversion"/> now mirrors the real subtitle
/// re-encode legacy's StreamBuilder performs (<c>MediaStream.SupportsSubtitleConversionTo</c>), so
/// (Chrome, mkv-h264-ac3-srt) resolves to <see cref="DivergenceClass.Equivalent"/> and
/// (Firefox, mp4-hevc-aac-srt) to ungated <see cref="DivergenceClass.KnownV2Limitation"/> (its HDR
/// tonemap is now derived in LegacyDecisionProjector) - both WITHOUT an allow-list entry. The third
/// former srt entry (Chrome mp4) had its subtitle gap closed too, but a distinct pre-existing
/// container bug it exposed keeps it allow-listed pending its own fix. The list must not grow
/// without a new, equally-documented, root-caused reason.
/// </remarks>
public sealed class OracleParityTests
{
    /// <summary>
    /// Every (DeviceProfile, Source) pair whose divergence is *expected* to classify as
    /// <see cref="DivergenceClass.PotentialRegression"/> or <see cref="DivergenceClass.Unexplained"/>,
    /// together with the reason that makes the divergence acceptable rather than a real regression.
    /// PR104: closing this allow-list (as opposed to loosening the test's assertion) is deliberate -
    /// any NEW case that lands here without being added to this dictionary fails the test, forcing a
    /// conscious decision instead of a silently-passing gate.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string DeviceProfile, string Source), string> ApprovedDivergences =
        new Dictionary<(string, string), string>
        {
            [("Chrome", "mp4-h264-ac3-aac-srt-2600k")] =
                "Subtitle text-format conversion (srt->vtt) is now MODELED (PR111c), so the historical " +
                "subtitle gap on this case is CLOSED - both engines convert and copy video. The residual " +
                "divergence (v2-only Remux transform + Container reason) is a SEPARATE, pre-existing bug " +
                "this PR merely exposed once video stopped being force-transcoded by burn-in: legacy's " +
                "StreamBuilder MUTATES the shared MediaSourceInfo.Container into a single value " +
                "(NormalizeMediaSourceFormatIntoSingleContainer, StreamBuilder.cs:836) and the shadow " +
                "flow runs legacy BEFORE snapshotting for v2 (ShadowPlaybackSessionPlanner.RunShadow, " +
                "and the oracle here), so v2 sees the degraded container 'mov' instead of this source's " +
                "real ffprobe set 'mov,mp4,m4a,3gp,3g2,mj2'; v2 then whole-string-compares it against " +
                "target 'mp4' and falsely flags a container remux. Legacy emits no container reason " +
                "(mp4 is a member of the real set). This masks whether v2 handles raw ffprobe container " +
                "strings at all - a CANARY-READINESS question (when v2 is the sole planner no legacy " +
                "normalizes first). FIX tracked as its own PR: (a) v2 containerChanged uses " +
                "ContainerHelper.ContainsContainer; (b) the shadow harness snapshots the source BEFORE " +
                "legacy mutates it - expected to move more than this one case, hence out of PR111c scope.",
            [("Chrome", "mp4-dvhe.08-eac3-15200k")] =
                "Dolby Vision (Profile 8.1) fallback on a non-DV client: legacy and v2 AGREE the DV " +
                "range must be transcoded away on Chrome; they only differ on the transcode target. " +
                "The gating axis is videoRange (legacy=HLG, v2=SDR). Legacy's 'HLG' is not a " +
                "deliberate HDR-preservation policy - it is an artifact of Enum.TryParse bitwise-ORing " +
                "the multi-value 'SDR|HDR10|HLG' EqualsAny VideoRangeType condition (ordinals " +
                "1|2|3 = 3, which coincidentally equals HLG's ordinal; VideoRangeType is not [Flags]). " +
                "v2 deliberately tonemaps any unsupported HDR source down to plain SDR " +
                "(PlaybackEngine, tonemap path) rather than trying to preserve an HDR10/HLG fallback " +
                "the target codec profile would accept - an acknowledged v2 simplification, not a " +
                "regression to match legacy's accidental number for. FOLLOW-UPS (tracked, not blocking " +
                "this gate): (1) v2 could recover HDR10 here since the source carries an HDR10 base " +
                "layer and Chrome's hevc/av1 profiles accept HDR10; (2) v2 picks av1 vs legacy's hevc " +
                "target because it takes OutputProfile.VideoCodecs[0] instead of preferring the " +
                "source's own codec when present (same 'prefer source codec' gap as legacy's " +
                "StreamInfo.TargetVideoCodec) - a separate PlaybackEngine target-selection fix.",
        };

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

            // PR104: 2 additional cases on a different device profile (Firefox instead of Chrome),
            // reusing the already-understood, already-vtt-normalized (PR103) source plus a second
            // vp9/vorbis/vtt source Firefox decodes natively - both direct play cleanly on both
            // engines, extending oracle coverage without touching the documented srt->vtt gap.
            ("Firefox", "mp4-h264-aac-vtt-2600k"), // direct play, second device profile
            ("Firefox", "mkv-vp9-vorbis-vtt-2600k"), // direct play, vp9/vorbis/mkv

            // PR111b: 3 new mandatory HDR/10-bit cases, promoted from "documented but untested" to
            // real oracle cases that actually run both engines and get classified.
            ("Chrome", "mp4-h264-hi10p-aac-5000k"), // H.264 10-bit (High 10 profile) - Chrome's h264
                                                     // CodecProfile explicitly allows "high 10"
            ("WebOS-23", "mp4-dvhe.08-eac3-15200k"), // Dolby Vision natively supported - WebOS-23's
                                                      // hevc CodecProfile allows DOVIWithHDR10
            ("Chrome", "mp4-dvhe.08-eac3-15200k"), // Dolby Vision NOT supported - Chrome's hevc
                                                    // CodecProfile only allows SDR|HDR10|HLG, forcing
                                                    // legacy to fall back/transcode the DOVI range
        };

        var metrics = new ShadowMetrics();
        var results = new List<(string DeviceProfile, string Source, ShadowDivergence Divergence)>();

        foreach (var (deviceProfile, source) in cases)
        {
            var options = await GetMediaOptions(deviceProfile, source);

            var stopwatch = Stopwatch.StartNew();

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
        // real finding of this PR, gated below by ApprovedDivergences instead.
        var (_, _, directPlayDivergence) = results.Single(r => r.DeviceProfile == "Chrome" && r.Source == "mp4-h264-aac-vtt-2600k");

        Assert.Equal(DivergenceClass.Equivalent, directPlayDivergence.Class);
        Assert.False(directPlayDivergence.MethodDiffers);
        Assert.Empty(directPlayDivergence.OnlyLegacy);
        Assert.Empty(directPlayDivergence.OnlyV2);
        Assert.Empty(directPlayDivergence.ReasonOnlyLegacy);
        Assert.Empty(directPlayDivergence.ReasonOnlyV2);

        // PR104 gate: every case's divergence must be Equivalent/ExpectedImprovement/
        // KnownV2Limitation, OR PotentialRegression/Unexplained with an explicit, written reason in
        // ApprovedDivergences. Zero unapproved PotentialRegression/Unexplained across the whole
        // oracle - not just the direct-play case above - is what makes this a real gate instead of a
        // report.
        foreach (var (deviceProfile, source, divergence) in results)
        {
            if (divergence.Class is not (DivergenceClass.PotentialRegression or DivergenceClass.Unexplained))
            {
                continue;
            }

            Assert.True(
                ApprovedDivergences.TryGetValue((deviceProfile, source), out var reason),
                $"({deviceProfile}, {source}) classified as {divergence.Class} ({divergence.Summary}) but is not in " +
                $"{nameof(ApprovedDivergences)}. Either this is a real regression to fix, or it needs an explicit, " +
                "written entry explaining why it's acceptable - never a silent widening of the assertion.");

            _output.WriteLine(FormattableString.Invariant(
                $"  (approved divergence) ({deviceProfile}, {source}) -> {divergence.Class}: {reason}"));
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
