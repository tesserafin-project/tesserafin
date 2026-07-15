using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Configuration;
using Reefin.Model.Dlna;
using Reefin.Playback.Decision;
using Reefin.Playback.Dlna;
using Reefin.Playback.Engine;
using Xunit;

namespace Reefin.Playback.Shadow.Tests;

/// <summary>
/// PR111d: the last playback gate before PR112 - a REAL shadow-mode hot-path PERFORMANCE gate,
/// complementing <see cref="OracleParityTests"/> (which gates CORRECTNESS/divergence classification
/// but never measured timing beyond a single coarse per-case stopwatch fed into
/// <see cref="ShadowMetrics"/>'s <c>&lt;1/&lt;5/&lt;10/&lt;25/&lt;50/&lt;100/&gt;=100ms</c> buckets -
/// too coarse for the sub-millisecond phases this gate actually observes). This test measures raw
/// per-phase <see cref="Stopwatch"/> timings and computes exact percentiles instead.
/// </summary>
/// <remarks>
/// <para>
/// HONEST SCOPING: "real scenarios" here means the REAL legacy <see cref="StreamBuilder"/> and the
/// REAL v2 <see cref="PlaybackEngine"/> running over real fixture data - the same 9-case oracle
/// harness fixtures shared with <see cref="OracleParityTests"/> via <see cref="OracleCaseFixtures"/>,
/// covering Direct Play, transcode (container/audio codec/video codec/secondary audio), subtitle
/// text conversion, and HDR/Dolby-Vision/10-bit sources. This is NOT a live-HTTP-session benchmark -
/// there is no ASP.NET pipeline, no real client, no real transcoder process involved. That is a
/// deliberate local-only scoping decision: CI is quota-blocked for this PR, so the gate must be
/// something a developer can run repeatedly and cheaply on a workstation. Also note: none of the 9
/// oracle cases is a DEDICATED pure-remux fixture; the closest available stand-in is the
/// (Chrome, mkv-h264-ac3-srt-2600k) container+audio-codec transcode case from the mkv-h264-ac3-srt
/// family - it is reused here as-is rather than overclaiming a remux-specific fixture that does not
/// exist in the Test Data set.
/// </para>
/// <para>
/// NO PRODUCTION CODE CHANGES. <c>ShadowPlaybackSessionPlanner.RunShadow</c> already isolates
/// mapping/engine/comparison into three separate <see cref="Stopwatch"/> instances (it only ever
/// logs their values on a budget overrun); this test mirrors that exact phase split without touching
/// that method or any other production type.
/// </para>
/// </remarks>
public sealed class ShadowPerformanceGateTests
{
    /// <summary>
    /// Iterations discarded per case before any measurement starts, to let .NET's tiered JIT
    /// (tier-0 quick-and-dirty compilation, promoted to optimized tier-1 code after a method is
    /// called often enough) settle. Without this, the FIRST few timed iterations would measure
    /// JIT compilation and tier-0 code, not steady-state hot-path performance - exactly the
    /// distinction the informational "first warm-up vs hot p95" log line below exists to show.
    /// </summary>
    private const int WarmupIterationsPerCase = 300;

    /// <summary>Hot, measured iterations per case, per round.</summary>
    private const int HotIterationsPerCaseRound = 120;

    /// <summary>
    /// Round-A-vs-round-B stability bound, expressed as a RATIO (max/min) rather than an absolute
    /// millisecond delta - an absolute threshold would be machine-dependent (a loaded CI runner or a
    /// throttled laptop would flake) and this gate must survive `dotnet test` on ordinary dev
    /// hardware under ordinary background load. 4x is generous: it tolerates GC pauses, OS
    /// scheduling noise, and thermal throttling while still catching an order-of-magnitude
    /// regression (the actual failure mode this gate exists to prevent).
    /// </summary>
    private const double RoundStabilityMaxRatio = 4.0;

    /// <summary>
    /// Added to both round's p95 (in ms) before taking their ratio, so a phase whose real timing is
    /// a handful of microseconds (e.g. mapping) does not turn ordinary sub-microsecond timer jitter
    /// into a huge, meaningless ratio (0.001ms vs 0.004ms is "4x" but is noise, not a regression).
    /// </summary>
    private const double RoundStabilityFloorMs = 0.05;

    /// <summary>
    /// Fraction of hot iterations allowed to exceed <see cref="PlaybackShadowOptions.MaxExecutionMs"/>
    /// before the gate fails. Not zero: a perf gate that demands literally zero slow iterations ever,
    /// across ~2000+ measured hot iterations on a shared dev machine, will eventually flake on GC or
    /// scheduler noise alone. 5% tolerates rare noise while still catching a SYSTEMIC (not
    /// occasional) budget blowout.
    /// </summary>
    private const double MaxOverrunFraction = 0.05;

    /// <summary>
    /// The pooled hot p95 total (mapping+engine+comparison) must stay under this fraction of the
    /// configured budget. Chosen well below 1.0: the measured phases here are real in-process C#
    /// calls with no I/O (sub-millisecond), while the budget defaults to 50ms - a budget sized for a
    /// production shadow run that can occasionally hit GC or contention. Requiring p95 to stay under
    /// half the budget leaves comfortable margin against day-to-day noise while still catching a
    /// regression that starts eating a meaningful fraction of the real budget.
    /// </summary>
    private const double BudgetMarginFraction = 0.5;

    private readonly ITestOutputHelper _output;

    public ShadowPerformanceGateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ShadowGate_HotPathPerformance_MeetsBudgetAndIsStable()
    {
        var maxExecutionMs = new PlaybackShadowOptions().MaxExecutionMs;

        // ---- Setup (untimed): one StreamBuilder + one PlaybackEngine reused across every
        // iteration and every case, mirroring production's single long-lived _engine
        // (ShadowPlaybackSessionPlanner._engine). Legacy's GetOptimalVideoStream runs EXACTLY ONCE
        // per case here - matching production (legacy runs once per real playback request) and
        // required because StreamBuilder mutates the shared MediaSourceInfo.Container in place
        // (see OracleCaseFixtures.ApprovedDivergences' Chrome/mp4-h264-ac3-aac-srt-2600k entry) -
        // calling it repeatedly per case would measure a progressively-mutated input, not a stable
        // one.
        var streamBuilder = OracleCaseFixtures.GetStreamBuilder();
        var engine = new PlaybackEngine();

        var caseSetups = new List<CaseSetup>();
        foreach (var (deviceProfile, source) in OracleCaseFixtures.Cases)
        {
            var options = await OracleCaseFixtures.GetMediaOptions(deviceProfile, source);
            var legacyStreamInfo = streamBuilder.GetOptimalVideoStream(options);
            var plan = legacyStreamInfo is null
                ? null
                : new PlaybackPlan(legacyStreamInfo.PlayMethod, legacyStreamInfo.TranscodeReasons, legacyStreamInfo);

            caseSetups.Add(new CaseSetup(deviceProfile, source, options, plan));
        }

        // ---- Warm-up (discarded): let tiered JIT settle. No try/catch anywhere in this method -
        // an exception here is a real bug and must fail the test, not be swallowed the way
        // production's RunShadowSafely swallows shadow-side exceptions.
        var firstWarmupTotalMsByCase = new Dictionary<string, double>();
        foreach (var setup in caseSetups)
        {
            for (var i = 0; i < WarmupIterationsPerCase; i++)
            {
                var iteration = RunIteration(engine, setup.Options, setup.Plan);
                if (i == 0)
                {
                    firstWarmupTotalMsByCase[CaseKey(setup)] = iteration.MappingMs + iteration.EngineMs + iteration.ComparisonMs;
                }
            }
        }

        // ---- Hot measurement: two independent rounds, pooled per phase within each round for a
        // real percentile sample size, plus kept per-case for the report.
        var roundA = RunHotRound(engine, caseSetups);
        var roundB = RunHotRound(engine, caseSetups);

        // ==== Gate 2: zero unexplained divergence, and deterministic classification per case ====
        foreach (var setup in caseSetups)
        {
            var key = CaseKey(setup);
            var classesSeen = roundA.ClassesByCase[key].Concat(roundB.ClassesByCase[key]).Distinct().ToList();

            Assert.True(
                classesSeen.Count == 1,
                $"({setup.DeviceProfile}, {setup.Source}) classified inconsistently across hot iterations: " +
                $"[{string.Join(", ", classesSeen)}]. A frozen input through a deterministic pipeline must " +
                "produce a single divergence class every time - inconsistency here means either the pipeline " +
                "is non-deterministic (a real bug) or state is leaking across iterations (a test bug).");

            var divergenceClass = classesSeen[0];
            if (divergenceClass is DivergenceClass.PotentialRegression or DivergenceClass.Unexplained)
            {
                Assert.True(
                    OracleCaseFixtures.ApprovedDivergences.ContainsKey((setup.DeviceProfile, setup.Source)),
                    $"({setup.DeviceProfile}, {setup.Source}) classified as {divergenceClass} under the hot-path " +
                    "perf gate but is not in OracleCaseFixtures.ApprovedDivergences. Either this is a real " +
                    "regression, or it needs the same explicit, written allow-list entry OracleParityTests " +
                    "requires - never a silent pass here.");
            }
        }

        // ==== Gate 3: budget not systemically exceeded ====
        var pooledHotTotalsMs = roundA.PooledTotalsMs.Concat(roundB.PooledTotalsMs).OrderBy(x => x).ToArray();
        var pooledP95TotalMs = Percentile(pooledHotTotalsMs, 0.95);
        var overrunFraction = pooledHotTotalsMs.Count(ms => ms > maxExecutionMs) / (double)pooledHotTotalsMs.Length;

        Assert.True(
            pooledP95TotalMs <= maxExecutionMs * BudgetMarginFraction,
            $"Pooled hot p95 total ({pooledP95TotalMs:F4}ms) exceeds {BudgetMarginFraction:P0} of the configured " +
            $"budget ({maxExecutionMs}ms -> {maxExecutionMs * BudgetMarginFraction:F2}ms margin). This is the " +
            "actual budget check: the shadow run must stay comfortably inside PlaybackShadowOptions.MaxExecutionMs.");

        Assert.True(
            overrunFraction <= MaxOverrunFraction,
            $"{overrunFraction:P1} of hot iterations exceeded the {maxExecutionMs}ms budget - more than the " +
            $"{MaxOverrunFraction:P0} tolerance for rare noise. This looks like a SYSTEMIC overrun, not noise.");

        // ==== Gate 4: stable hot p95 across rounds A and B, per phase, via relative tolerance ====
        AssertPhaseStable("mapping", roundA.PooledMappingMs, roundB.PooledMappingMs);
        AssertPhaseStable("engine", roundA.PooledEngineMs, roundB.PooledEngineMs);
        AssertPhaseStable("comparison", roundA.PooledComparisonMs, roundB.PooledComparisonMs);

        // ==== Report (pasteable for the PR111d journal entry) ====
        var report = BuildReport(caseSetups, roundA, roundB, maxExecutionMs, pooledP95TotalMs, overrunFraction, firstWarmupTotalMsByCase);
        _output.WriteLine(report);
    }

    private static void AssertPhaseStable(string phaseName, IReadOnlyList<double> roundAMs, IReadOnlyList<double> roundBMs)
    {
        var sortedA = roundAMs.OrderBy(x => x).ToArray();
        var sortedB = roundBMs.OrderBy(x => x).ToArray();

        var p95A = Percentile(sortedA, 0.95) + RoundStabilityFloorMs;
        var p95B = Percentile(sortedB, 0.95) + RoundStabilityFloorMs;

        var ratio = Math.Max(p95A, p95B) / Math.Min(p95A, p95B);

        Assert.True(
            ratio <= RoundStabilityMaxRatio,
            $"Phase '{phaseName}' hot p95 drifted between rounds beyond the {RoundStabilityMaxRatio}x tolerance: " +
            $"round A p95={Percentile(sortedA, 0.95):F4}ms, round B p95={Percentile(sortedB, 0.95):F4}ms " +
            $"(floored ratio={ratio:F2}x). This is an order-of-magnitude drift check, not a tight budget - a " +
            "failure here means something changed the hot path's cost, not ordinary machine noise.");
    }

    private static HotRoundResult RunHotRound(PlaybackEngine engine, IReadOnlyList<CaseSetup> caseSetups)
    {
        var result = new HotRoundResult();

        foreach (var setup in caseSetups)
        {
            var key = CaseKey(setup);
            var mapping = new List<double>(HotIterationsPerCaseRound);
            var engineTimes = new List<double>(HotIterationsPerCaseRound);
            var comparison = new List<double>(HotIterationsPerCaseRound);
            var classes = new List<DivergenceClass>(HotIterationsPerCaseRound);

            for (var i = 0; i < HotIterationsPerCaseRound; i++)
            {
                var iteration = RunIteration(engine, setup.Options, setup.Plan);
                mapping.Add(iteration.MappingMs);
                engineTimes.Add(iteration.EngineMs);
                comparison.Add(iteration.ComparisonMs);
                classes.Add(iteration.DivergenceClass);

                result.PooledMappingMs.Add(iteration.MappingMs);
                result.PooledEngineMs.Add(iteration.EngineMs);
                result.PooledComparisonMs.Add(iteration.ComparisonMs);
                result.PooledTotalsMs.Add(iteration.MappingMs + iteration.EngineMs + iteration.ComparisonMs);
            }

            result.MappingByCase[key] = mapping;
            result.EngineByCase[key] = engineTimes;
            result.ComparisonByCase[key] = comparison;
            result.ClassesByCase[key] = classes;
        }

        return result;
    }

    /// <summary>
    /// One timed iteration, phase-by-phase, mirroring <c>ShadowPlaybackSessionPlanner.RunShadow</c>
    /// exactly (mapping: ToCapabilities/ToConstraints/ToSnapshot-per-source/ToContext; engine:
    /// Decide; comparison: LegacyDecisionProjector.Project + V2DecisionProjector.Project +
    /// ShadowComparer.Compare). Deliberately has NO try/catch: a thrown exception here must fail
    /// the test, which is how "zero exceptions" becomes a real, checked property instead of an
    /// assumption.
    /// </summary>
    private static IterationResult RunIteration(PlaybackEngine engine, MediaOptions options, PlaybackPlan? plan)
    {
        var mappingStopwatch = Stopwatch.StartNew();
        var capabilities = DlnaPlaybackAdapter.ToCapabilities(options.Profile);
        var constraints = DlnaPlaybackAdapter.ToConstraints(options);
        var sources = options.MediaSources.Select(DlnaPlaybackAdapter.ToSnapshot).ToList();
        var context = DlnaPlaybackAdapter.ToContext(options.ItemId, Guid.Empty, options.MediaSourceId, MediaKind.Video, PlaybackEngine.EngineVersion);
        mappingStopwatch.Stop();

        var engineStopwatch = Stopwatch.StartNew();
        var decision = engine.Decide(context, capabilities, sources, constraints);
        engineStopwatch.Stop();

        var comparisonStopwatch = Stopwatch.StartNew();
        var legacyVector = LegacyDecisionProjector.Project(plan);
        var v2Vector = V2DecisionProjector.Project(decision);
        var divergence = ShadowComparer.Compare(legacyVector, v2Vector);
        comparisonStopwatch.Stop();

        return new IterationResult(
            mappingStopwatch.Elapsed.TotalMilliseconds,
            engineStopwatch.Elapsed.TotalMilliseconds,
            comparisonStopwatch.Elapsed.TotalMilliseconds,
            divergence.Class);
    }

    private static string CaseKey(CaseSetup setup) => setup.DeviceProfile + "|" + setup.Source;

    /// <summary>
    /// Exact percentile on an ASCENDING-sorted sample: <c>p95 = sorted[ceil(0.95*n) - 1]</c>. No
    /// interpolation, no bucket approximation (unlike <see cref="ShadowMetrics"/>'s coarse
    /// histogram) - this is the whole point of measuring raw timings in this test.
    /// </summary>
    private static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(p * sortedAscending.Count) - 1;
        rank = Math.Clamp(rank, 0, sortedAscending.Count - 1);
        return sortedAscending[rank];
    }

    private static string BuildReport(
        IReadOnlyList<CaseSetup> caseSetups,
        HotRoundResult roundA,
        HotRoundResult roundB,
        int maxExecutionMs,
        double pooledP95TotalMs,
        double overrunFraction,
        IReadOnlyDictionary<string, double> firstWarmupTotalMsByCase)
    {
        var report = new StringBuilder();
        report.AppendLine("PR111d shadow hot-path performance gate:");
        report.AppendLine(FormattableString.Invariant(
            $"  budget (PlaybackShadowOptions.MaxExecutionMs)={maxExecutionMs}ms, pooled hot p95 total={pooledP95TotalMs:F4}ms, overrun fraction={overrunFraction:P2}"));
        report.AppendLine();

        foreach (var setup in caseSetups)
        {
            var key = CaseKey(setup);
            report.AppendLine(FormattableString.Invariant($"  ({setup.DeviceProfile}, {setup.Source}):"));

            AppendPhaseLine(report, "mapping", roundA.MappingByCase[key], roundB.MappingByCase[key]);
            AppendPhaseLine(report, "engine", roundA.EngineByCase[key], roundB.EngineByCase[key]);
            AppendPhaseLine(report, "comparison", roundA.ComparisonByCase[key], roundB.ComparisonByCase[key]);

            var classesA = roundA.ClassesByCase[key].Distinct().ToList();
            var classesB = roundB.ClassesByCase[key].Distinct().ToList();
            report.AppendLine(FormattableString.Invariant(
                $"    divergence class: round A={string.Join(",", classesA)} round B={string.Join(",", classesB)}"));

            if (firstWarmupTotalMsByCase.TryGetValue(key, out var firstWarmupMs))
            {
                var hotP95TotalMs = Percentile(roundA.MappingByCase[key].Concat(roundB.MappingByCase[key]).OrderBy(x => x).ToArray(), 0.95)
                    + Percentile(roundA.EngineByCase[key].Concat(roundB.EngineByCase[key]).OrderBy(x => x).ToArray(), 0.95)
                    + Percentile(roundA.ComparisonByCase[key].Concat(roundB.ComparisonByCase[key]).OrderBy(x => x).ToArray(), 0.95);

                // Informational only (JIT warm-up evidence) - NOT asserted, since a cold first-call
                // duration is expected to be (often dramatically) higher than steady-state hot p95
                // and is not itself a regression signal.
                report.AppendLine(FormattableString.Invariant(
                    $"    warm-up evidence: first warm-up iteration total={firstWarmupMs:F4}ms vs hot p95 total={hotP95TotalMs:F4}ms"));
            }
        }

        return report.ToString();
    }

    private static void AppendPhaseLine(StringBuilder report, string phaseName, IReadOnlyList<double> roundAMs, IReadOnlyList<double> roundBMs)
    {
        var sortedA = roundAMs.OrderBy(x => x).ToArray();
        var sortedB = roundBMs.OrderBy(x => x).ToArray();

        report.AppendLine(FormattableString.Invariant(
            $"    {phaseName,-10} A: p50={Percentile(sortedA, 0.50):F4}ms p95={Percentile(sortedA, 0.95):F4}ms p99={Percentile(sortedA, 0.99):F4}ms | B: p50={Percentile(sortedB, 0.50):F4}ms p95={Percentile(sortedB, 0.95):F4}ms p99={Percentile(sortedB, 0.99):F4}ms"));
    }

    private readonly record struct IterationResult(double MappingMs, double EngineMs, double ComparisonMs, DivergenceClass DivergenceClass);

    private sealed record CaseSetup(string DeviceProfile, string Source, MediaOptions Options, PlaybackPlan? Plan);

    private sealed class HotRoundResult
    {
        public List<double> PooledMappingMs { get; } = new();

        public List<double> PooledEngineMs { get; } = new();

        public List<double> PooledComparisonMs { get; } = new();

        public List<double> PooledTotalsMs { get; } = new();

        public Dictionary<string, List<double>> MappingByCase { get; } = new();

        public Dictionary<string, List<double>> EngineByCase { get; } = new();

        public Dictionary<string, List<double>> ComparisonByCase { get; } = new();

        public Dictionary<string, List<DivergenceClass>> ClassesByCase { get; } = new();
    }
}
