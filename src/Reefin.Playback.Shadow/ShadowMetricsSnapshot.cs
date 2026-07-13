using System;
using System.Collections.Generic;
using System.Linq;

namespace Reefin.Playback.Shadow;

/// <summary>
/// An immutable point-in-time view of <see cref="ShadowMetrics"/>' counters, suitable for a single
/// structured log line or for test assertions.
/// </summary>
/// <param name="TotalExecutions">The total number of completed shadow runs (classified or thrown).</param>
/// <param name="DivergenceClassCounts">Count of classified runs per <see cref="DivergenceClass"/>.</param>
/// <param name="ExceptionCount">The number of shadow runs that threw before classification.</param>
/// <param name="BudgetExceededCount">The number of shadow runs whose measured duration exceeded the configured budget.</param>
/// <param name="DurationBucketCounts">Coarse duration histogram: [&lt;1ms, &lt;5ms, &lt;10ms, &lt;25ms, &lt;50ms, &lt;100ms, &gt;=100ms].</param>
/// <param name="ApproxP95Bucket">The bucket label approximating the p95 shadow-run duration.</param>
public sealed record ShadowMetricsSnapshot(
    long TotalExecutions,
    IReadOnlyDictionary<DivergenceClass, long> DivergenceClassCounts,
    long ExceptionCount,
    long BudgetExceededCount,
    IReadOnlyList<long> DurationBucketCounts,
    string ApproxP95Bucket)
{
    /// <summary>
    /// Renders the snapshot as a single human-readable line suitable for the periodic aggregate log.
    /// </summary>
    /// <returns>A single-line, human-readable summary of every counter and the duration histogram.</returns>
    public string ToSummaryString()
    {
        var perClass = string.Join(
            ", ",
            DivergenceClassCounts.Select(kvp => FormattableString.Invariant($"{kvp.Key}={kvp.Value}")));
        var buckets = string.Join(",", DurationBucketCounts);

        return FormattableString.Invariant(
            $"executions={TotalExecutions}, {perClass}, exceptions={ExceptionCount}, budgetExceeded={BudgetExceededCount}, durationBucketsMs=[{buckets}], approxP95={ApproxP95Bucket}");
    }
}
