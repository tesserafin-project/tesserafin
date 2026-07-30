using System;
using System.Collections.Generic;

namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// The outcome of <see cref="ShadowPerformancePolicy.EvaluateBudget"/>: every hard rule that failed,
/// plus the measurements those rules were computed from.
/// </summary>
/// <param name="Failures">One message per violated hard rule; empty means the samples pass.</param>
/// <param name="SampleCount">Total pooled hot iterations judged.</param>
/// <param name="PooledP95TotalMs">The pooled hot p95 total, in milliseconds.</param>
/// <param name="MaxExecutionMs">The configured budget the rules were evaluated against.</param>
/// <param name="BudgetMarginMs">The absolute margin the pooled p95 total had to stay under.</param>
/// <param name="OverrunFraction">The fraction of iterations that exceeded the budget outright.</param>
internal sealed record ShadowBudgetVerdict(
    IReadOnlyList<string> Failures,
    int SampleCount,
    double PooledP95TotalMs,
    double MaxExecutionMs,
    double BudgetMarginMs,
    double OverrunFraction)
{
    /// <summary>Gets a value indicating whether every hard budget rule held.</summary>
    public bool IsWithinBudget => Failures.Count == 0;

    /// <summary>Renders every violated rule as one assertable message.</summary>
    /// <returns>The concatenated failure messages, or an empty string when the samples pass.</returns>
    public string Describe() => string.Join(Environment.NewLine, Failures);
}
