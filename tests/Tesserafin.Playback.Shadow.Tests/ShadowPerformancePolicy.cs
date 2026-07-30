using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// The performance POLICY behind <see cref="ShadowPerformanceGateTests"/>, extracted as a pure
/// function of timing samples so the pass/fail decision can be exercised with SYNTHETIC data
/// (<see cref="ShadowPerformancePolicyTests"/>) instead of being provable only by repeatedly running
/// a wall-clock benchmark and hoping the machine cooperates.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS POLICY ASSERTS (hard, in <see cref="EvaluateBudget"/>) is exclusively
/// BUDGET-RELATIVE - the pooled hot p95 total must stay inside a fraction of
/// <c>PlaybackShadowOptions.MaxExecutionMs</c>, and no more than a small fraction of iterations may
/// exceed that budget outright. Both are ABSOLUTE thresholds expressed in the product's own units,
/// so they mean the same thing on a quiet workstation and on a contended shared runner.
/// </para>
/// <para>
/// WHAT THIS POLICY ONLY REPORTS (soft, in <see cref="MeasureRoundStability"/>) is the
/// round-to-round p95 drift per phase. It used to be a hard assertion; issue #145 showed that a
/// RELATIVE, round-versus-round statistic measures the runner's scheduling stability rather than the
/// product's cost. Two properties made it the wrong gate, in both directions:
/// </para>
/// <list type="bullet">
/// <item><description>
/// It FALSE-FAILS on host contention. The compared samples are pooled across all 9 oracle cases
/// (1080 per round), so the p95 sits above 54 samples; a hosted runner that deschedules more than
/// 5% of one round's iterations moves that p95 by an order of magnitude while every real budget
/// clause still passes. Four such failures were recorded on <c>master</c>-equivalent test
/// assemblies, with either round as the slow side (5.44x, 4.61x, 20.96x, 6.94x).
/// </description></item>
/// <item><description>
/// It MISSES the regression it claimed to catch. A genuine slowdown of the hot path affects both
/// rounds equally, leaving their ratio near 1.0x. The ratio was blind to exactly the sustained,
/// reproducible regression a performance gate exists to detect; the budget clauses catch that case.
/// </description></item>
/// </list>
/// <para>
/// Deliberate, stated limit of the hard rules: a sustained regression is rejected only once it
/// breaches the configured budget margin. Sub-budget drift (say a uniform 2x, 0.3ms to 0.6ms against
/// a 25ms margin) is ACCEPTED. That is not a coverage loss versus the previous gate - the old ratio
/// was blind to uniform drift too, at any magnitude - and it is the price of a threshold that means
/// the same thing on every machine.
/// </para>
/// </remarks>
internal static class ShadowPerformancePolicy
{
    /// <summary>
    /// Exact percentile: <c>p95 = sortedAscending[ceil(0.95*n) - 1]</c>. No interpolation, no bucket
    /// approximation (unlike <c>ShadowMetrics</c>'s coarse histogram) - exact percentiles over raw
    /// timings are the whole point of this gate. The sample is sorted internally, so callers cannot
    /// silently feed an unsorted list and get a meaningless answer.
    /// </summary>
    /// <param name="samples">The timing samples, in any order.</param>
    /// <param name="p">The percentile, as a fraction in <c>(0, 1]</c>.</param>
    /// <returns>The percentile value, or <c>0</c> when <paramref name="samples"/> is empty.</returns>
    public static double Percentile(IReadOnlyList<double> samples, double p)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return 0;
        }

        var sorted = samples.ToArray();
        Array.Sort(sorted);

        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        rank = Math.Clamp(rank, 0, sorted.Length - 1);
        return sorted[rank];
    }

    /// <summary>
    /// Evaluates the two HARD budget rules over the pooled per-iteration totals of both hot rounds.
    /// Empty or non-finite input is REJECTED rather than treated as a healthy zero, so a harness bug
    /// that collects no samples can never produce a vacuous pass.
    /// </summary>
    /// <param name="roundATotalsMs">Round A's per-iteration totals, in milliseconds.</param>
    /// <param name="roundBTotalsMs">Round B's per-iteration totals, in milliseconds.</param>
    /// <param name="maxExecutionMs">The configured <c>PlaybackShadowOptions.MaxExecutionMs</c> budget.</param>
    /// <param name="budgetMarginFraction">The fraction of the budget the pooled p95 total must stay under.</param>
    /// <param name="maxOverrunFraction">The fraction of iterations allowed to exceed the budget outright.</param>
    /// <returns>The verdict, carrying every failed rule and the measurements behind them.</returns>
    public static ShadowBudgetVerdict EvaluateBudget(
        IReadOnlyList<double> roundATotalsMs,
        IReadOnlyList<double> roundBTotalsMs,
        double maxExecutionMs,
        double budgetMarginFraction,
        double maxOverrunFraction)
    {
        ArgumentNullException.ThrowIfNull(roundATotalsMs);
        ArgumentNullException.ThrowIfNull(roundBTotalsMs);

        var failures = new List<string>();

        if (roundATotalsMs.Count == 0 || roundBTotalsMs.Count == 0)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"No timing samples to judge: round A contributed {roundATotalsMs.Count} iteration(s), round B contributed {roundBTotalsMs.Count}. An empty sample is a harness failure, never a pass."));
        }

        var pooled = roundATotalsMs.Concat(roundBTotalsMs).ToArray();

        if (pooled.Any(ms => !double.IsFinite(ms) || ms < 0))
        {
            failures.Add(
                "Timing samples contain a negative or non-finite value. A Stopwatch-derived duration is always " +
                "finite and non-negative, so this is a harness failure, never a pass.");
        }

        if (!double.IsFinite(maxExecutionMs) || maxExecutionMs <= 0)
        {
            failures.Add(FormattableString.Invariant(
                $"The configured budget ({maxExecutionMs}ms) is not a positive, finite number of milliseconds."));
        }

        if (!double.IsFinite(budgetMarginFraction) || budgetMarginFraction <= 0 || budgetMarginFraction > 1)
        {
            failures.Add(FormattableString.Invariant(
                $"The budget margin fraction ({budgetMarginFraction}) must lie in (0, 1]."));
        }

        if (!double.IsFinite(maxOverrunFraction) || maxOverrunFraction < 0 || maxOverrunFraction > 1)
        {
            failures.Add(FormattableString.Invariant(
                $"The maximum overrun fraction ({maxOverrunFraction}) must lie in [0, 1]."));
        }

        if (failures.Count > 0)
        {
            return new ShadowBudgetVerdict(failures, pooled.Length, 0, maxExecutionMs, 0, 0);
        }

        var budgetMarginMs = maxExecutionMs * budgetMarginFraction;
        var pooledP95TotalMs = Percentile(pooled, 0.95);
        var overrunFraction = pooled.Count(ms => ms > maxExecutionMs) / (double)pooled.Length;

        if (pooledP95TotalMs > budgetMarginMs)
        {
            failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pooled hot p95 total ({pooledP95TotalMs:F4}ms over {pooled.Length} iterations) exceeds {budgetMarginFraction:P0} of the configured budget ({maxExecutionMs}ms -> {budgetMarginMs:F2}ms margin).")
                + " This is the actual budget check: the shadow run must stay comfortably inside"
                + " PlaybackShadowOptions.MaxExecutionMs. Unlike a round-versus-round ratio, this threshold is"
                + " absolute, so a contended runner cannot inflate it without the work genuinely taking that long.");
        }

        if (overrunFraction > maxOverrunFraction)
        {
            failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{overrunFraction:P1} of the {pooled.Length} hot iterations exceeded the {maxExecutionMs}ms budget - more than the {maxOverrunFraction:P0} tolerance for rare noise.")
                + " This looks like a SYSTEMIC overrun, not noise.");
        }

        return new ShadowBudgetVerdict(failures, pooled.Length, pooledP95TotalMs, maxExecutionMs, budgetMarginMs, overrunFraction);
    }

    /// <summary>
    /// Measures - and only measures - how far one phase's hot p95 drifted between the two rounds.
    /// The result is DIAGNOSTIC: it is printed in the gate's report and is never asserted on, because
    /// on shared hardware it reflects the runner's scheduling stability at least as much as the
    /// product's cost (see the remarks on <see cref="ShadowPerformancePolicy"/>).
    /// </summary>
    /// <param name="roundAMs">Round A's per-iteration phase durations, in milliseconds.</param>
    /// <param name="roundBMs">Round B's per-iteration phase durations, in milliseconds.</param>
    /// <param name="floorMs">Added to both p95 values before the ratio, so sub-microsecond timer jitter cannot produce a huge ratio out of nothing.</param>
    /// <returns>Both rounds' p95 and their floored ratio.</returns>
    public static RoundStabilityMeasurement MeasureRoundStability(
        IReadOnlyList<double> roundAMs,
        IReadOnlyList<double> roundBMs,
        double floorMs)
    {
        ArgumentNullException.ThrowIfNull(roundAMs);
        ArgumentNullException.ThrowIfNull(roundBMs);

        var p95A = Percentile(roundAMs, 0.95);
        var p95B = Percentile(roundBMs, 0.95);

        var flooredA = p95A + floorMs;
        var flooredB = p95B + floorMs;

        var ratio = Math.Min(flooredA, flooredB) <= 0
            ? 1.0
            : Math.Max(flooredA, flooredB) / Math.Min(flooredA, flooredB);

        return new RoundStabilityMeasurement(p95A, p95B, ratio);
    }
}
