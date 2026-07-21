using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// PR115d: trip/no-trip/reset/defaults/disabled matrix for <see cref="PlaybackStopThresholdGuard"/> -
/// see that type's remarks (and <see cref="PlaybackStopThresholdOptions"/>'s) for the exact
/// stateless-evaluation, config-change-to-reset semantics these tests exercise.
/// </summary>
public class PlaybackStopThresholdGuardTests
{
    [Fact]
    public void Evaluate_NoActivity_NotTripped()
    {
        var options = new PlaybackShadowOptions();
        var metrics = new PlaybackOperationalMetrics();
        var guard = BuildGuard(() => options, metrics);

        Assert.False(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_BelowMinimumSampleSize_NotTrippedEvenAt100PercentErrorRate()
    {
        var options = new PlaybackShadowOptions();
        options.StopThresholds.MinimumSampleSize = 20;
        var metrics = new PlaybackOperationalMetrics();
        // Every single v2 attempt so far failed - a 100% adapter-error rate - but well under the
        // minimum sample size, so this must not trip: a 1-in-1 failure is noise, not a signal.
        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError);
        var guard = BuildGuard(() => options, metrics);

        Assert.False(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_AdapterErrorRateAtOrAboveThresholdWithEnoughSamples_Tripped()
    {
        var options = new PlaybackShadowOptions();
        options.StopThresholds.MinimumSampleSize = 10;
        options.StopThresholds.AdapterErrorRateThreshold = 0.10;
        var metrics = new PlaybackOperationalMetrics();
        for (var i = 0; i < 9; i++)
        {
            metrics.RecordServed();
        }

        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError); // 1/10 = 10% == threshold
        var guard = BuildGuard(() => options, metrics);

        Assert.True(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_AdapterErrorRateBelowThreshold_NotTripped()
    {
        var options = new PlaybackShadowOptions();
        options.StopThresholds.MinimumSampleSize = 10;
        options.StopThresholds.AdapterErrorRateThreshold = 0.50;
        var metrics = new PlaybackOperationalMetrics();
        for (var i = 0; i < 9; i++)
        {
            metrics.RecordServed();
        }

        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError); // 1/10 = 10% < 50%
        var guard = BuildGuard(() => options, metrics);

        Assert.False(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_TranscodeStartFailureRateAtOrAboveThresholdWithEnoughSamples_Tripped()
    {
        var options = new PlaybackShadowOptions();
        options.StopThresholds.MinimumSampleSize = 5;
        options.StopThresholds.TranscodeStartFailureRateThreshold = 0.20;
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordTranscodeStart(failed: true);
        metrics.RecordTranscodeStart(failed: false);
        metrics.RecordTranscodeStart(failed: false);
        metrics.RecordTranscodeStart(failed: false);
        metrics.RecordTranscodeStart(failed: false); // 1/5 = 20% == threshold
        var guard = BuildGuard(() => options, metrics);

        Assert.True(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_Disabled_NeverTripsRegardlessOfRate()
    {
        var options = new PlaybackShadowOptions();
        options.StopThresholds.Enabled = false;
        options.StopThresholds.MinimumSampleSize = 1;
        options.StopThresholds.AdapterErrorRateThreshold = 0.01;
        var metrics = new PlaybackOperationalMetrics();
        for (var i = 0; i < 20; i++)
        {
            metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError);
        }

        var guard = BuildGuard(() => options, metrics);

        Assert.False(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_TrippedThenConfigDisablesGuard_NoLongerTripped()
    {
        // The guard is stateless-per-call: it recomputes from the live options accessor and the
        // live metrics on every Evaluate() call. Because tripping stops further v2 attempts, the
        // counters that produced a trip stay frozen - the ONLY way to clear a trip is a config
        // change picked up by the same live accessor, exactly as this test does.
        var options = new PlaybackShadowOptions();
        options.StopThresholds.MinimumSampleSize = 1;
        options.StopThresholds.AdapterErrorRateThreshold = 0.10;
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError);
        var guard = BuildGuard(() => options, metrics);
        Assert.True(guard.Evaluate());

        options.StopThresholds.Enabled = false;

        Assert.False(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_TrippedThenThresholdRaisedByConfig_NoLongerTripped()
    {
        var options = new PlaybackShadowOptions();
        options.StopThresholds.MinimumSampleSize = 10;
        options.StopThresholds.AdapterErrorRateThreshold = 0.10;
        var metrics = new PlaybackOperationalMetrics();
        for (var i = 0; i < 9; i++)
        {
            metrics.RecordServed();
        }

        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError); // 1/10 = 10% == threshold
        var guard = BuildGuard(() => options, metrics);
        Assert.True(guard.Evaluate());

        // Same frozen counters (10 attempts, 1 error, 10% rate) - but the operator raised the
        // threshold above the observed rate via a live config change, no restart, same as the
        // Mode kill switch's own "no restart required" discipline.
        options.StopThresholds.AdapterErrorRateThreshold = 0.50;

        Assert.False(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_TrippedThenMinimumSampleSizeRaisedAboveCurrentAttempts_NoLongerTripped()
    {
        var options = new PlaybackShadowOptions();
        options.StopThresholds.MinimumSampleSize = 1;
        options.StopThresholds.AdapterErrorRateThreshold = 0.10;
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError);
        var guard = BuildGuard(() => options, metrics);
        Assert.True(guard.Evaluate());

        options.StopThresholds.MinimumSampleSize = 1000;

        Assert.False(guard.Evaluate());
    }

    [Fact]
    public void Evaluate_DefaultOptions_EnabledWithSaneThresholds()
    {
        // PR115d requirement: the guard defaults to ON - an operator must make an explicit, visible
        // choice to disable it, not the other way around.
        var options = new PlaybackStopThresholdOptions();

        Assert.True(options.Enabled);
        Assert.True(options.AdapterErrorRateThreshold is > 0.0 and <= 1.0);
        Assert.True(options.TranscodeStartFailureRateThreshold is > 0.0 and <= 1.0);
        Assert.True(options.MinimumSampleSize > 0);
    }

    private static PlaybackStopThresholdGuard BuildGuard(System.Func<PlaybackShadowOptions> optionsAccessor, PlaybackOperationalMetrics metrics) =>
        new(optionsAccessor, metrics, NullLogger<PlaybackStopThresholdGuard>.Instance);
}
