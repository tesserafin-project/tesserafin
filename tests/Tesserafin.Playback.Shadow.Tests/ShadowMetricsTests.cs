using System;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// Unit tests for <see cref="ShadowMetrics"/> (PR100): per-<see cref="DivergenceClass"/> counters,
/// the duration histogram, and the periodic-summary cadence.
/// </summary>
public sealed class ShadowMetricsTests
{
    [Fact]
    public void RecordExecution_IncrementsTotalAndClassCounters()
    {
        var metrics = new ShadowMetrics();

        metrics.RecordExecution(DivergenceClass.Equivalent, TimeSpan.FromMilliseconds(2), budgetExceeded: false);
        metrics.RecordExecution(DivergenceClass.PotentialRegression, TimeSpan.FromMilliseconds(2), budgetExceeded: false);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(2, snapshot.TotalExecutions);
        Assert.Equal(1, snapshot.DivergenceClassCounts[DivergenceClass.Equivalent]);
        Assert.Equal(1, snapshot.DivergenceClassCounts[DivergenceClass.PotentialRegression]);
        Assert.Equal(0, snapshot.ExceptionCount);
    }

    [Fact]
    public void RecordException_IncrementsTotalAndExceptionCounter_NotAnyDivergenceClass()
    {
        var metrics = new ShadowMetrics();

        metrics.RecordException(TimeSpan.FromMilliseconds(1), budgetExceeded: false);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(1, snapshot.TotalExecutions);
        Assert.Equal(1, snapshot.ExceptionCount);
        Assert.All(snapshot.DivergenceClassCounts.Values, count => Assert.Equal(0, count));
    }

    [Fact]
    public void RecordExecution_BudgetExceeded_IncrementsBudgetCounter()
    {
        var metrics = new ShadowMetrics();

        metrics.RecordExecution(DivergenceClass.Equivalent, TimeSpan.FromMilliseconds(200), budgetExceeded: true);
        metrics.RecordExecution(DivergenceClass.Equivalent, TimeSpan.FromMilliseconds(1), budgetExceeded: false);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(1, snapshot.BudgetExceededCount);
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(4, 1)]
    [InlineData(9, 2)]
    [InlineData(20, 3)]
    [InlineData(49, 4)]
    [InlineData(99, 5)]
    [InlineData(150, 6)]
    public void RecordExecution_PlacesDurationInExpectedBucket(double ms, int expectedBucketIndex)
    {
        var metrics = new ShadowMetrics();

        metrics.RecordExecution(DivergenceClass.Equivalent, TimeSpan.FromMilliseconds(ms), budgetExceeded: false);

        var snapshot = metrics.GetSnapshot();

        for (var i = 0; i < snapshot.DurationBucketCounts.Count; i++)
        {
            Assert.Equal(i == expectedBucketIndex ? 1 : 0, snapshot.DurationBucketCounts[i]);
        }
    }

    [Fact]
    public void RecordExecution_ReturnsNullSnapshot_UntilSummaryIntervalBoundary()
    {
        var metrics = new ShadowMetrics();

        for (var i = 0; i < ShadowMetrics.SummaryIntervalExecutions - 1; i++)
        {
            var result = metrics.RecordExecution(DivergenceClass.Equivalent, TimeSpan.FromMilliseconds(1), budgetExceeded: false);
            Assert.Null(result);
        }

        var boundary = metrics.RecordExecution(DivergenceClass.Equivalent, TimeSpan.FromMilliseconds(1), budgetExceeded: false);

        Assert.NotNull(boundary);
        Assert.Equal(ShadowMetrics.SummaryIntervalExecutions, boundary!.TotalExecutions);
    }

    [Fact]
    public void GetSnapshot_NoExecutions_ApproxP95IsNotAvailable()
    {
        var metrics = new ShadowMetrics();

        var snapshot = metrics.GetSnapshot();

        Assert.Equal("n/a", snapshot.ApproxP95Bucket);
    }

    [Fact]
    public void ToSummaryString_ContainsAllCounters()
    {
        var metrics = new ShadowMetrics();
        metrics.RecordExecution(DivergenceClass.Equivalent, TimeSpan.FromMilliseconds(1), budgetExceeded: false);
        metrics.RecordException(TimeSpan.FromMilliseconds(1), budgetExceeded: false);

        var summary = metrics.GetSnapshot().ToSummaryString();

        Assert.Contains("executions=2", summary, StringComparison.Ordinal);
        Assert.Contains("exceptions=1", summary, StringComparison.Ordinal);
        Assert.Contains("Equivalent=1", summary, StringComparison.Ordinal);
    }
}
