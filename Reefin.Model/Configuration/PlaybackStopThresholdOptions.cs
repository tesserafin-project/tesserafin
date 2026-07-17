using System;

namespace Reefin.Model.Configuration;

/// <summary>
/// PR115d: configures the operational stop-threshold guard for the v2 canary - an automatic,
/// config-driven safety net that forces every live playback request back to legacy (the same
/// observable effect as the <see cref="PlaybackShadowOptions.Mode"/> kill switch) once the v2 live
/// path's own error signals cross an operator-chosen threshold, without waiting for a human to
/// notice and flip the kill switch by hand. A sibling of <see cref="PlaybackShadowOptions"/> rather
/// than a merge into it - <see cref="PlaybackShadowOptions.StopThresholds"/> holds one instance -
/// so the two concerns (what role v2 plays; when it auto-disqualifies itself) stay independently
/// readable and independently testable, matching this configuration surface's existing
/// one-concern-per-type shape (<c>TrickplayOptions</c>, <c>PlaybackShadowOptions</c> itself).
/// </summary>
/// <remarks>
/// <para>
/// <b>Evaluation, not a persisted latch.</b> The guard this configures (<c>PlaybackStopThresholdGuard</c>,
/// <c>Reefin.MediaEncoding.Playback</c>) recomputes "is the guard tripped right now?" from the live
/// values of this options object and the live cumulative counters in <c>PlaybackOperationalMetrics</c>
/// on every request - it never persists a separate "tripped" bit anywhere. Two consequences of that
/// design, both deliberate:
/// </para>
/// <list type="bullet">
/// <item>Once tripped, the guard is <b>sticky in practice</b>, not because of any latch, but because
/// tripping stops every further v2 attempt - the counters that produced the trip (v2 attempts,
/// v2 adapter errors, v2 transcode start failures) simply stop moving, so the computed rate never
/// dilutes back down on its own. This mirrors a real circuit breaker: it does not reset itself just
/// because the danger has technically passed.</item>
/// <item>The only way to clear a trip is an operator config change that this class's live-read
/// callers already pick up without a restart - the same "no restart required" discipline
/// <see cref="PlaybackShadowOptions.Mode"/>'s kill switch already relies on. Concretely: raise the
/// relevant threshold, raise <see cref="MinimumSampleSize"/> above the current attempt count, or set
/// <see cref="Enabled"/> to <see langword="false"/>. There is deliberately no separate "reset" API -
/// one clearly documented mechanism (config) is safer than two (config AND some out-of-band reset
/// call that could silently disagree with what the config says).</item>
/// </list>
/// <para>
/// PR104's clamp-don't-throw convention for this configuration surface applies here identically -
/// see <see cref="PlaybackShadowOptions"/>'s own remarks for why: no established "throw on invalid
/// config" precedent, no logger available to report a corrected value at bind time, and clamping is
/// deterministic/idempotent.
/// </para>
/// </remarks>
public class PlaybackStopThresholdOptions
{
    private double _adapterErrorRateThreshold = 0.10;
    private double _transcodeStartFailureRateThreshold = 0.20;
    private int _minimumSampleSize = 20;

    /// <summary>
    /// Gets or sets a value indicating whether the stop-threshold guard evaluates at all. Defaults
    /// to <see langword="true"/> - deliberately the opposite default from <see cref="PlaybackShadowOptions.Enabled"/>:
    /// PR115d's scope requires the guard to protect production canary traffic by default, with an
    /// operator having to make an explicit, visible choice to turn the safety net off, rather than
    /// having to remember to turn it on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the fraction (0.0-1.0) of v2-attempted live requests that may end in an
    /// <c>AdapterError</c> (<c>Reefin.MediaEncoding.Playback.PlaybackLiveFallbackReason</c>) before
    /// the guard trips. "v2-attempted" means the request passed every earlier gate (kill switch,
    /// cohort, resolvable plan, source id match, Dolby Vision exclusion) and reached the adapter
    /// (<c>Reefin.Playback.Dlna.PlaybackExecutionPlanAdapter.ToStreamInfo</c>) - the denominator is
    /// served-by-v2 count plus adapter-error count, not the count of every live request. Not a
    /// <c>cref</c> to either type: this project (<c>Reefin.Model</c>) sits below both in the
    /// dependency graph and must not reference them. Defaults
    /// to 0.10 (10%): the adapter is expected to succeed on essentially every eligible plan (PR115b's
    /// executable-parity invariant exists precisely to make adapter failures rare), so a double-digit
    /// error rate is already a strong signal something is wrong. Clamped to [0, 1] on set, following
    /// this type's PR104 clamp-don't-throw convention.
    /// </summary>
    public double AdapterErrorRateThreshold
    {
        get => _adapterErrorRateThreshold;
        set => _adapterErrorRateThreshold = double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Gets or sets the fraction (0.0-1.0) of ffmpeg transcode starts for v2-served sessions that may
    /// fail to actually launch before the guard trips - see <c>PlaybackOperationalMetrics</c>'s remarks
    /// for exactly what "failed to start" means and how it is observed
    /// (<c>ITranscodeManager.TranscodingJobStarted</c>/<c>TranscodingJobEnded</c>). Defaults to 0.20
    /// (20%): transcode start failures have causes entirely outside the v2/legacy decision itself
    /// (disk space, hardware encoder contention, a malformed source), so this threshold is
    /// deliberately looser than <see cref="AdapterErrorRateThreshold"/> - it exists to catch a v2
    /// plan that is systematically producing FFmpeg arguments ffmpeg cannot start with, not every
    /// transient environmental failure. Clamped to [0, 1] on set, following this type's PR104
    /// clamp-don't-throw convention.
    /// </summary>
    public double TranscodeStartFailureRateThreshold
    {
        get => _transcodeStartFailureRateThreshold;
        set => _transcodeStartFailureRateThreshold = double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Gets or sets the minimum number of samples (v2 attempts, for <see cref="AdapterErrorRateThreshold"/>;
    /// v2 transcode starts, for <see cref="TranscodeStartFailureRateThreshold"/>) required before a
    /// rate is trusted enough to trip the guard. Defaults to 20: without a floor, the very first v2
    /// request failing would already be a "100% error rate" and would trip the guard on a single
    /// unlucky sample - exactly the kind of premature, noisy trip an operator opening a 1% canary
    /// cohort cannot afford to chase. Clamped to a minimum of 1 on set (mirrors
    /// <see cref="PlaybackShadowOptions.MaxExecutionMs"/>'s own floor) - zero would make any single
    /// failure trip the guard, which is never the intent of a misconfigured value.
    /// </summary>
    public int MinimumSampleSize
    {
        get => _minimumSampleSize;
        set => _minimumSampleSize = Math.Max(1, value);
    }
}
