using System;
using System.Collections.Generic;
using System.Threading;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// Thread-safe aggregate counters and a coarse duration histogram for the shadow playback
/// comparison introduced in PR98 and hardened in PR100. One instance is shared across every
/// shadow run performed by a <c>ShadowPlaybackSessionPlanner</c> singleton, so all mutation goes
/// through <see cref="Interlocked"/> rather than a lock.
/// </summary>
/// <remarks>
/// Individual shadow runs are cheap (microseconds to low milliseconds) and happen on the hot
/// playback-decision path, so this class intentionally avoids allocations, locks, and anything
/// that could become contended under load.
/// </remarks>
public sealed class ShadowMetrics
{
    /// <summary>
    /// The number of completed shadow executions (successful classification or exception) between
    /// each periodic aggregate log emission. Kept as a simple execution-count cadence rather than a
    /// wall-clock timer for simplicity, per PR100 spec.
    /// </summary>
    public const int SummaryIntervalExecutions = 500;

    private static readonly double[] BucketUpperBoundsMs = { 1, 5, 10, 25, 50, 100 };
    private static readonly string[] BucketLabels =
    {
        "<1ms", "<5ms", "<10ms", "<25ms", "<50ms", "<100ms", ">=100ms",
    };

    // Indexed by (int)DivergenceClass. DivergenceClass has 5 members as of PR93/PR98.
    private readonly long[] _divergenceClassCounts = new long[Enum.GetValues<DivergenceClass>().Length];

    // One bucket per BucketUpperBoundsMs entry, plus one overflow bucket for ">=100ms".
    private readonly long[] _durationBuckets = new long[BucketUpperBoundsMs.Length + 1];

    private long _totalExecutions;
    private long _exceptionCount;
    private long _budgetExceededCount;

    /// <summary>
    /// Records a shadow run that completed with a classified divergence (including
    /// <see cref="DivergenceClass.Equivalent"/>). Returns a snapshot if this execution is the one
    /// that crosses a <see cref="SummaryIntervalExecutions"/> boundary, so the caller can emit a
    /// single periodic aggregate log line; otherwise returns <see langword="null"/>.
    /// </summary>
    /// <param name="divergenceClass">The classification the shadow comparison produced for this run.</param>
    /// <param name="elapsed">The total measured duration of this shadow run.</param>
    /// <param name="budgetExceeded">Whether <paramref name="elapsed"/> exceeded the configured time budget.</param>
    /// <returns>A periodic summary snapshot if this run crosses the aggregation boundary; otherwise <see langword="null"/>.</returns>
    public ShadowMetricsSnapshot? RecordExecution(DivergenceClass divergenceClass, TimeSpan elapsed, bool budgetExceeded)
    {
        Interlocked.Increment(ref _divergenceClassCounts[(int)divergenceClass]);
        RecordDuration(elapsed);
        if (budgetExceeded)
        {
            Interlocked.Increment(ref _budgetExceededCount);
        }

        return CompleteExecution();
    }

    /// <summary>
    /// Records a shadow run that threw before it could be classified. Returns a snapshot on the
    /// same periodic cadence as <see cref="RecordExecution"/>.
    /// </summary>
    /// <param name="elapsed">The total measured duration until the shadow run threw.</param>
    /// <param name="budgetExceeded">Whether <paramref name="elapsed"/> exceeded the configured time budget.</param>
    /// <returns>A periodic summary snapshot if this run crosses the aggregation boundary; otherwise <see langword="null"/>.</returns>
    public ShadowMetricsSnapshot? RecordException(TimeSpan elapsed, bool budgetExceeded)
    {
        Interlocked.Increment(ref _exceptionCount);
        RecordDuration(elapsed);
        if (budgetExceeded)
        {
            Interlocked.Increment(ref _budgetExceededCount);
        }

        return CompleteExecution();
    }

    /// <summary>
    /// Takes an immutable snapshot of the current counters, usable at any time (not only on the
    /// periodic cadence) - primarily for tests and diagnostics.
    /// </summary>
    /// <returns>The current counters and duration histogram.</returns>
    public ShadowMetricsSnapshot GetSnapshot()
    {
        var divergenceCounts = new Dictionary<DivergenceClass, long>();
        foreach (var divergenceClass in Enum.GetValues<DivergenceClass>())
        {
            divergenceCounts[divergenceClass] = Interlocked.Read(ref _divergenceClassCounts[(int)divergenceClass]);
        }

        var buckets = new long[_durationBuckets.Length];
        for (var i = 0; i < _durationBuckets.Length; i++)
        {
            buckets[i] = Interlocked.Read(ref _durationBuckets[i]);
        }

        var total = Interlocked.Read(ref _totalExecutions);

        return new ShadowMetricsSnapshot(
            total,
            divergenceCounts,
            Interlocked.Read(ref _exceptionCount),
            Interlocked.Read(ref _budgetExceededCount),
            buckets,
            ApproximateP95Bucket(buckets, total));
    }

    private void RecordDuration(TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        var bucketIndex = BucketUpperBoundsMs.Length;
        for (var i = 0; i < BucketUpperBoundsMs.Length; i++)
        {
            if (ms < BucketUpperBoundsMs[i])
            {
                bucketIndex = i;
                break;
            }
        }

        Interlocked.Increment(ref _durationBuckets[bucketIndex]);
    }

    private ShadowMetricsSnapshot? CompleteExecution()
    {
        var total = Interlocked.Increment(ref _totalExecutions);
        return total % SummaryIntervalExecutions == 0 ? GetSnapshot() : null;
    }

    /// <summary>
    /// Derives an approximate p95 bucket label from the histogram: the narrowest bucket whose
    /// cumulative count covers at least 95% of all observations. This is a coarse approximation
    /// (bucket-resolution, not exact), sufficient for a periodic health signal.
    /// </summary>
    private static string ApproximateP95Bucket(IReadOnlyList<long> buckets, long total)
    {
        if (total == 0)
        {
            return "n/a";
        }

        var threshold = total * 0.95;
        long cumulative = 0;
        for (var i = 0; i < buckets.Count; i++)
        {
            cumulative += buckets[i];
            if (cumulative >= threshold)
            {
                return BucketLabels[i];
            }
        }

        return BucketLabels[^1];
    }
}
