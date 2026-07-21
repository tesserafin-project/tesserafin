using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tesserafin.Model.Configuration;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// PR115d: the operational stop-threshold guard - evaluates, on every live playback decision, whether
/// the v2 live path's own observed error signals (<see cref="PlaybackOperationalMetrics"/>) have
/// crossed an operator-configured threshold (<see cref="PlaybackStopThresholdOptions"/>) and, if so,
/// forces legacy - the same observable effect as the <see cref="PlaybackShadowOptions.Mode"/> kill
/// switch, but tripped automatically instead of by an operator's own hand. Consulted by
/// <c>MediaInfoHelper.ResolveServedStreamInfo</c> immediately after the kill switch check and before
/// resolving a v2 plan - see that method for the exact ordering and why (a tripped guard must never
/// still pay the cost of resolving a plan it is about to discard).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless evaluation, not a persisted latch.</b> <see cref="Evaluate"/> recomputes its answer
/// from scratch on every call, from the live options (<c>optionsAccessor</c>, read the
/// same way <c>ShadowPlaybackSessionPlanner</c> already reads <see cref="PlaybackShadowOptions"/> -
/// a <see cref="Func{T}"/> re-invoked per call, not cached in the constructor, so a config change
/// takes effect on the very next request, no restart required) and the live cumulative counters in
/// <c>metrics</c>. There is no separate "tripped" bit stored anywhere. See
/// <see cref="PlaybackStopThresholdOptions"/>'s remarks for the operational consequence: the guard is
/// sticky in practice once tripped (a trip stops further v2 attempts, which stops the counters that
/// produced the trip from ever moving again), and the only way to un-trip it is a live-read config
/// change - raise a threshold, raise <see cref="PlaybackStopThresholdOptions.MinimumSampleSize"/>
/// above the current attempt count, or set <see cref="PlaybackStopThresholdOptions.Enabled"/> to
/// <see langword="false"/>.
/// </para>
/// <para>
/// <b>Observability.</b> A trip must never be silent. <see cref="Evaluate"/> logs once, at
/// <see cref="LogLevel.Critical"/>, on the false-to-true transition - not on every call, which would
/// spam one log line per live playback request for as long as the guard stays tripped. That
/// transition is tracked with a single <see cref="Interlocked"/> flag, the only piece of mutable
/// state this type owns; it exists purely to gate the log line, never the trip decision itself,
/// which stays fully re-derived from <c>optionsAccessor</c>/<c>metrics</c>
/// every time. The same flag also means a later re-trip (guard clears via config, then a fresh
/// regression trips it again) logs again, rather than only ever once per process lifetime.
/// </para>
/// </remarks>
public sealed class PlaybackStopThresholdGuard
{
    private readonly Func<PlaybackShadowOptions> _optionsAccessor;
    private readonly PlaybackOperationalMetrics _metrics;
    private readonly ILogger<PlaybackStopThresholdGuard> _logger;
    private int _wasTripped;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStopThresholdGuard"/> class.
    /// </summary>
    /// <param name="optionsAccessor">
    /// Resolves the live <see cref="PlaybackShadowOptions"/> (whose <see cref="PlaybackShadowOptions.StopThresholds"/>
    /// this guard reads) on every <see cref="Evaluate"/> call - a <see cref="Func{T}"/>, not a
    /// snapshot, for the same "no restart required" reason <c>ShadowPlaybackSessionPlanner</c>
    /// already takes one for the same options type.
    /// </param>
    /// <param name="metrics">The live cumulative operational metrics this guard evaluates against.</param>
    /// <param name="logger">Where the loud, transition-only trip log line is written.</param>
    public PlaybackStopThresholdGuard(
        Func<PlaybackShadowOptions> optionsAccessor,
        PlaybackOperationalMetrics metrics,
        ILogger<PlaybackStopThresholdGuard> logger)
    {
        _optionsAccessor = optionsAccessor;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates whether the guard is tripped right now, logging loudly on the false-to-true
    /// transition - see this type's remarks for the full semantics. Safe to call from both the live
    /// decision path and the admin diagnostics endpoint: idempotent with respect to logging, and
    /// cheap (a handful of <see cref="Interlocked"/> reads, no allocation).
    /// </summary>
    /// <returns><see langword="true"/> if legacy must be forced for this request.</returns>
    public bool Evaluate()
    {
        // Defensive null-coalesce, not a documented contract: PlaybackShadowOptions.StopThresholds
        // defaults to a fresh instance, but this guard sits directly upstream of the live streaming
        // path's kill switch discipline ("v2 must never break the live path") - an NRE from a
        // hypothetically null StopThresholds (a config binder replacing the whole PlaybackShadow
        // section, for instance) must never be what breaks that discipline. A fallback default is as
        // safe a substitute as any: it evaluates as "guard enabled, sane defaults", never as
        // "guard silently disabled".
        var options = _optionsAccessor().StopThresholds ?? new PlaybackStopThresholdOptions();
        if (!options.Enabled)
        {
            ClearTrippedFlag();
            return false;
        }

        var adapterAttempts = _metrics.ServedByV2Count + _metrics.FallbackReasonCount(PlaybackLiveFallbackReason.AdapterError);
        var adapterErrors = _metrics.FallbackReasonCount(PlaybackLiveFallbackReason.AdapterError);
        var adapterTripped = adapterAttempts >= options.MinimumSampleSize
            && Rate(adapterErrors, adapterAttempts) >= options.AdapterErrorRateThreshold;

        var transcodeAttempts = _metrics.TranscodeStartAttemptsV2;
        var transcodeFailures = _metrics.TranscodeStartFailuresV2;
        var transcodeTripped = transcodeAttempts >= options.MinimumSampleSize
            && Rate(transcodeFailures, transcodeAttempts) >= options.TranscodeStartFailureRateThreshold;

        var tripped = adapterTripped || transcodeTripped;

        if (tripped)
        {
            LogIfNewlyTripped(adapterTripped, adapterErrors, adapterAttempts, transcodeTripped, transcodeFailures, transcodeAttempts);
        }
        else
        {
            ClearTrippedFlag();
        }

        return tripped;
    }

    private static double Rate(long numerator, long denominator) => denominator == 0 ? 0.0 : (double)numerator / denominator;

    private void LogIfNewlyTripped(bool adapterTripped, long adapterErrors, long adapterAttempts, bool transcodeTripped, long transcodeFailures, long transcodeAttempts)
    {
        if (Interlocked.CompareExchange(ref _wasTripped, 1, 0) != 0)
        {
            return;
        }

        _logger.LogCritical(
            "PR115d stop-threshold guard TRIPPED - forcing legacy for every live request until an operator changes configuration. " +
            "AdapterError: tripped={AdapterTripped} rate={AdapterErrors}/{AdapterAttempts}. " +
            "TranscodeStartFailure: tripped={TranscodeTripped} rate={TranscodeFailures}/{TranscodeAttempts}.",
            adapterTripped,
            adapterErrors,
            adapterAttempts,
            transcodeTripped,
            transcodeFailures,
            transcodeAttempts);
    }

    private void ClearTrippedFlag() => Interlocked.Exchange(ref _wasTripped, 0);
}
