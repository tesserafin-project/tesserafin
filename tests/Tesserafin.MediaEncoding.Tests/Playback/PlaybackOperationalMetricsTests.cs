using Tesserafin.MediaEncoding.Playback;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// PR115d: aggregation tests for <see cref="PlaybackOperationalMetrics"/> - mirrors
/// <c>ShadowMetricsTests</c>' shape (counter increments, snapshot correctness, derived rates).
/// </summary>
public class PlaybackOperationalMetricsTests
{
    [Fact]
    public void RecordServed_IncrementsServedByV2Count()
    {
        var metrics = new PlaybackOperationalMetrics();

        metrics.RecordServed();
        metrics.RecordServed();

        Assert.Equal(2, metrics.ServedByV2Count);
    }

    [Fact]
    public void RecordFallback_IncrementsOnlyTheGivenReasonCount()
    {
        var metrics = new PlaybackOperationalMetrics();

        metrics.RecordFallback(PlaybackLiveFallbackReason.KillSwitch);
        metrics.RecordFallback(PlaybackLiveFallbackReason.KillSwitch);
        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError);

        Assert.Equal(2, metrics.FallbackReasonCount(PlaybackLiveFallbackReason.KillSwitch));
        Assert.Equal(1, metrics.FallbackReasonCount(PlaybackLiveFallbackReason.AdapterError));
        Assert.Equal(0, metrics.FallbackReasonCount(PlaybackLiveFallbackReason.NoAuthoritativeRecord));
    }

    [Fact]
    public void RecordTranscodeStart_Failed_IncrementsBothAttemptsAndFailures()
    {
        var metrics = new PlaybackOperationalMetrics();

        metrics.RecordTranscodeStart(failed: true);

        Assert.Equal(1, metrics.TranscodeStartAttemptsV2);
        Assert.Equal(1, metrics.TranscodeStartFailuresV2);
    }

    [Fact]
    public void RecordTranscodeStart_Succeeded_IncrementsOnlyAttempts()
    {
        var metrics = new PlaybackOperationalMetrics();

        metrics.RecordTranscodeStart(failed: false);

        Assert.Equal(1, metrics.TranscodeStartAttemptsV2);
        Assert.Equal(0, metrics.TranscodeStartFailuresV2);
    }

    [Fact]
    public void GetSnapshot_NoActivity_AllCountersZeroAndRatesZero()
    {
        var metrics = new PlaybackOperationalMetrics();

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(0, snapshot.ServedByV2Count);
        Assert.Equal(0, snapshot.ServedByLegacyCount);
        Assert.Equal(0, snapshot.TotalDecisions);
        Assert.Equal(0, snapshot.AdapterAttempts);
        Assert.Equal(0.0, snapshot.AdapterErrorRate);
        Assert.Equal(0.0, snapshot.TranscodeStartFailureRate);
    }

    [Fact]
    public void GetSnapshot_ReflectsEveryRecordedFallbackReason()
    {
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordFallback(PlaybackLiveFallbackReason.KillSwitch);
        metrics.RecordFallback(PlaybackLiveFallbackReason.DolbyVisionExclusion);
        metrics.RecordFallback(PlaybackLiveFallbackReason.DolbyVisionExclusion);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(1, snapshot.FallbackReasonCounts[PlaybackLiveFallbackReason.KillSwitch]);
        Assert.Equal(2, snapshot.FallbackReasonCounts[PlaybackLiveFallbackReason.DolbyVisionExclusion]);
        Assert.Equal(3, snapshot.ServedByLegacyCount);
    }

    [Fact]
    public void AdapterErrorRate_DenominatorIsServedPlusAdapterErrorOnly_OtherReasonsExcluded()
    {
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordServed();
        metrics.RecordServed();
        metrics.RecordServed();
        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError);
        // These must NOT count toward the adapter-attempts denominator: the request never reached
        // the adapter at all for these reasons.
        metrics.RecordFallback(PlaybackLiveFallbackReason.KillSwitch);
        metrics.RecordFallback(PlaybackLiveFallbackReason.NoAuthoritativeRecord);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(4, snapshot.AdapterAttempts);
        Assert.Equal(0.25, snapshot.AdapterErrorRate);
    }

    [Fact]
    public void TranscodeStartFailureRate_ComputedFromTranscodeCountersOnly()
    {
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordTranscodeStart(failed: true);
        metrics.RecordTranscodeStart(failed: false);
        metrics.RecordTranscodeStart(failed: false);
        metrics.RecordTranscodeStart(failed: false);

        var snapshot = metrics.GetSnapshot();

        Assert.Equal(4, snapshot.TranscodeStartAttemptsV2);
        Assert.Equal(1, snapshot.TranscodeStartFailuresV2);
        Assert.Equal(0.25, snapshot.TranscodeStartFailureRate);
    }

    [Fact]
    public void ToSummaryString_ContainsCoreCounters()
    {
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordServed();
        metrics.RecordFallback(PlaybackLiveFallbackReason.KillSwitch);

        var summary = metrics.GetSnapshot().ToSummaryString();

        Assert.Contains("servedByV2=1", summary, System.StringComparison.Ordinal);
        Assert.Contains("servedByLegacy=1", summary, System.StringComparison.Ordinal);
        Assert.Contains("KillSwitch=1", summary, System.StringComparison.Ordinal);
    }
}
