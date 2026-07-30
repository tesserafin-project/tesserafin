using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// Deterministic, wall-clock-free coverage of <see cref="ShadowPerformancePolicy"/> - the pass/fail
/// decision that <see cref="ShadowPerformanceGateTests"/> feeds real measurements into. Every case
/// here supplies SYNTHETIC timing samples, so the policy's behaviour is proven by construction
/// rather than inferred from a benchmark that happened to be green on the machine that ran it.
/// </summary>
/// <remarks>
/// The pair that matters for issue #145 is <see cref="OneContaminatedRound_IsNotAProductFailure"/>
/// versus <see cref="SustainedRegressionBreachingTheBudget_IsRejected"/>: scheduler contamination of
/// a single round is accepted (and would have tripped the removed ratio assertion), while a slowdown
/// present in BOTH rounds - invisible to that ratio - is rejected.
/// </remarks>
public sealed class ShadowPerformancePolicyTests
{
    /// <summary>The default <c>PlaybackShadowOptions.MaxExecutionMs</c>.</summary>
    private const double BudgetMs = 50;

    /// <summary>The fraction of the budget the pooled hot p95 total must stay under.</summary>
    private const double MarginFraction = 0.5;

    /// <summary>The fraction of hot iterations allowed to exceed the budget outright.</summary>
    private const double MaxOverrunFraction = 0.05;

    /// <summary>The floor the (diagnostic-only) round-stability ratio adds to both p95 values.</summary>
    private const double FloorMs = 0.05;

    /// <summary>Iterations per synthetic round, matching the real gate's 9 cases x 120 iterations.</summary>
    private const int IterationsPerRound = 1080;

    [Fact]
    public void HealthyFastSamples_AreAccepted()
    {
        var verdict = Evaluate(Constant(IterationsPerRound, 0.30), Constant(IterationsPerRound, 0.30));

        Assert.True(verdict.IsWithinBudget, verdict.Describe());
        Assert.Equal(0.30, verdict.PooledP95TotalMs, 6);
        Assert.Equal(0, verdict.OverrunFraction);
        Assert.Equal(25.0, verdict.BudgetMarginMs, 6);
        Assert.Equal(2 * IterationsPerRound, verdict.SampleCount);
    }

    [Fact]
    public void SymmetricHealthyRounds_AreAcceptedAndReportAFlatRatio()
    {
        var roundA = Jittered(IterationsPerRound, 0.28, 0.32);
        var roundB = Jittered(IterationsPerRound, 0.28, 0.32);

        var verdict = Evaluate(roundA, roundB);
        var stability = ShadowPerformancePolicy.MeasureRoundStability(roundA, roundB, FloorMs);

        Assert.True(verdict.IsWithinBudget, verdict.Describe());
        Assert.True(stability.FlooredRatio < 1.2, $"expected a near-flat ratio, got {stability.FlooredRatio:F2}x");
    }

    /// <summary>
    /// The exact shape of every hosted failure on issue #145: one round has a minority of
    /// scheduler-descheduled iterations, every real budget clause still holds. The policy must
    /// accept it - while the diagnostic ratio still shows the drift, and is large enough that the
    /// REMOVED assertion would have failed the build on it.
    /// </summary>
    /// <param name="contaminateRoundA">Whether the contaminated round is A (as in the 20.96x and 6.94x failures) or B (5.44x, 4.61x).</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneContaminatedRound_IsNotAProductFailure(bool contaminateRoundA)
    {
        var clean = Constant(IterationsPerRound, 0.30);
        var contaminated = WithOutliers(IterationsPerRound, 0.30, outlierCount: 65, outlierMs: 3.0);

        var roundA = contaminateRoundA ? contaminated : clean;
        var roundB = contaminateRoundA ? clean : contaminated;

        var verdict = Evaluate(roundA, roundB);
        var stability = ShadowPerformancePolicy.MeasureRoundStability(roundA, roundB, FloorMs);

        Assert.True(verdict.IsWithinBudget, verdict.Describe());
        Assert.True(
            verdict.PooledP95TotalMs <= verdict.BudgetMarginMs,
            $"pooled p95 {verdict.PooledP95TotalMs:F4}ms should stay inside the {verdict.BudgetMarginMs:F2}ms margin");
        Assert.Equal(0, verdict.OverrunFraction);

        // The removed Gate 4 compared these two p95 values against a 4.0x bound. This is the
        // false-red it produced: harmless contention in ONE round, no budget clause breached.
        Assert.True(
            stability.FlooredRatio > 4.0,
            $"expected the diagnostic ratio to exceed the old 4.0x bound, got {stability.FlooredRatio:F2}x");
    }

    [Fact]
    public void SustainedP95AboveTheBudgetMargin_IsRejectedByTheMarginRule()
    {
        var verdict = Evaluate(Constant(IterationsPerRound, 30.0), Constant(IterationsPerRound, 30.0));

        Assert.False(verdict.IsWithinBudget);
        Assert.Contains(verdict.Failures, f => f.Contains("Pooled hot p95 total", StringComparison.Ordinal));

        // 30ms is over the 25ms margin but under the 50ms budget, so ONLY the margin rule fires.
        Assert.Equal(0, verdict.OverrunFraction);
        Assert.DoesNotContain(verdict.Failures, f => f.Contains("SYSTEMIC overrun", StringComparison.Ordinal));
    }

    [Fact]
    public void TooManyIterationsAboveTheRealBudget_IsRejectedByTheOverrunRule()
    {
        var roundA = WithOutliers(IterationsPerRound, 0.30, outlierCount: 100, outlierMs: 60.0);
        var roundB = WithOutliers(IterationsPerRound, 0.30, outlierCount: 100, outlierMs: 60.0);

        var verdict = Evaluate(roundA, roundB);

        Assert.False(verdict.IsWithinBudget);
        Assert.True(verdict.OverrunFraction > MaxOverrunFraction);
        Assert.Contains(verdict.Failures, f => f.Contains("SYSTEMIC overrun", StringComparison.Ordinal));
    }

    /// <summary>
    /// A slowdown present in BOTH rounds - the regression a performance gate exists to catch, and
    /// precisely the case the removed round-versus-round ratio was blind to.
    /// </summary>
    [Fact]
    public void SustainedRegressionBreachingTheBudget_IsRejected()
    {
        var roundA = Constant(IterationsPerRound, 40.0);
        var roundB = Constant(IterationsPerRound, 40.0);

        var verdict = Evaluate(roundA, roundB);
        var stability = ShadowPerformancePolicy.MeasureRoundStability(roundA, roundB, FloorMs);

        Assert.False(verdict.IsWithinBudget);
        Assert.Contains(verdict.Failures, f => f.Contains("Pooled hot p95 total", StringComparison.Ordinal));

        // The semantic defect of the removed assertion, stated as an executable fact: a uniform
        // 133x slowdown over the healthy 0.30ms baseline leaves the round ratio at 1.00x.
        Assert.Equal(1.0, stability.FlooredRatio, 6);
    }

    /// <summary>
    /// The deliberate, stated limit of the hard rules: drift that stays inside the budget margin is
    /// accepted. This is not a coverage loss versus the removed ratio, which was equally blind to a
    /// uniform slowdown at ANY magnitude - see the 1.00x ratio asserted here on a doubled hot path.
    /// </summary>
    [Fact]
    public void SustainedSubBudgetDrift_IsAcceptedByDesign()
    {
        var roundA = Constant(IterationsPerRound, 0.60);
        var roundB = Constant(IterationsPerRound, 0.60);

        var verdict = Evaluate(roundA, roundB);
        var stability = ShadowPerformancePolicy.MeasureRoundStability(roundA, roundB, FloorMs);

        Assert.True(verdict.IsWithinBudget, verdict.Describe());
        Assert.Equal(1.0, stability.FlooredRatio, 6);
    }

    [Theory]
    [InlineData(0, IterationsPerRound)]
    [InlineData(IterationsPerRound, 0)]
    [InlineData(0, 0)]
    public void EmptyRounds_AreRejectedRatherThanPassingVacuously(int roundACount, int roundBCount)
    {
        var verdict = Evaluate(Constant(roundACount, 0.30), Constant(roundBCount, 0.30));

        Assert.False(verdict.IsWithinBudget);
        Assert.Contains(verdict.Failures, f => f.Contains("No timing samples to judge", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void NonFiniteOrNegativeSamples_AreRejected(double badSample)
    {
        var roundA = Constant(IterationsPerRound, 0.30);
        roundA[0] = badSample;

        var verdict = Evaluate(roundA, Constant(IterationsPerRound, 0.30));

        Assert.False(verdict.IsWithinBudget);
        Assert.Contains(verdict.Failures, f => f.Contains("negative or non-finite", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.0, MarginFraction, MaxOverrunFraction)]
    [InlineData(-50.0, MarginFraction, MaxOverrunFraction)]
    [InlineData(BudgetMs, 0.0, MaxOverrunFraction)]
    [InlineData(BudgetMs, 1.5, MaxOverrunFraction)]
    [InlineData(BudgetMs, MarginFraction, -0.1)]
    [InlineData(BudgetMs, MarginFraction, 1.5)]
    public void InvalidConfiguration_IsRejected(double budgetMs, double marginFraction, double maxOverrunFraction)
    {
        var samples = Constant(IterationsPerRound, 0.30);

        var verdict = ShadowPerformancePolicy.EvaluateBudget(samples, samples, budgetMs, marginFraction, maxOverrunFraction);

        Assert.False(verdict.IsWithinBudget);
    }

    [Fact]
    public void NullSamples_Throw()
    {
        var samples = Constant(1, 0.30);

        Assert.Throws<ArgumentNullException>(
            () => ShadowPerformancePolicy.EvaluateBudget(null!, samples, BudgetMs, MarginFraction, MaxOverrunFraction));
        Assert.Throws<ArgumentNullException>(
            () => ShadowPerformancePolicy.EvaluateBudget(samples, null!, BudgetMs, MarginFraction, MaxOverrunFraction));
    }

    /// <summary>
    /// The percentile is the exact <c>sorted[ceil(p*n) - 1]</c> rank the previous gate used, and it
    /// no longer depends on the caller having sorted the sample first.
    /// </summary>
    [Fact]
    public void Percentile_IsExactAndSortsItsInput()
    {
        var descending = Enumerable.Range(1, 100).Select(i => (double)(101 - i)).ToList();

        Assert.Equal(95, ShadowPerformancePolicy.Percentile(descending, 0.95));
        Assert.Equal(50, ShadowPerformancePolicy.Percentile(descending, 0.50));
        Assert.Equal(100, ShadowPerformancePolicy.Percentile(descending, 1.0));
        Assert.Equal(0, ShadowPerformancePolicy.Percentile(new List<double>(), 0.95));
    }

    private static ShadowBudgetVerdict Evaluate(IReadOnlyList<double> roundA, IReadOnlyList<double> roundB) =>
        ShadowPerformancePolicy.EvaluateBudget(roundA, roundB, BudgetMs, MarginFraction, MaxOverrunFraction);

    private static List<double> Constant(int count, double ms) => Enumerable.Repeat(ms, count).ToList();

    /// <summary>
    /// A round whose last <paramref name="outlierCount"/> iterations were descheduled. 65 of 1080 is
    /// just over 6%, so it pushes that round's own p95 (the 54-samples-from-the-top rank) onto the
    /// outlier plateau - the mechanism behind every hosted failure recorded on issue #145.
    /// </summary>
    /// <param name="count">Total iterations in the round.</param>
    /// <param name="baseMs">The healthy per-iteration duration.</param>
    /// <param name="outlierCount">How many iterations were descheduled.</param>
    /// <param name="outlierMs">The descheduled iterations' duration.</param>
    /// <returns>The synthetic round.</returns>
    private static List<double> WithOutliers(int count, double baseMs, int outlierCount, double outlierMs)
    {
        var samples = Constant(count, baseMs);
        for (var i = count - outlierCount; i < count; i++)
        {
            samples[i] = outlierMs;
        }

        return samples;
    }

    /// <summary>
    /// Deterministic (seed-free) spread across a narrow healthy band, so "symmetric healthy rounds"
    /// is not a degenerate all-identical sample.
    /// </summary>
    /// <param name="count">Total iterations in the round.</param>
    /// <param name="minMs">The band's lower bound.</param>
    /// <param name="maxMs">The band's upper bound.</param>
    /// <returns>The synthetic round.</returns>
    private static List<double> Jittered(int count, double minMs, double maxMs) =>
        Enumerable.Range(0, count).Select(i => minMs + ((maxMs - minMs) * (i % 10) / 9.0)).ToList();
}
