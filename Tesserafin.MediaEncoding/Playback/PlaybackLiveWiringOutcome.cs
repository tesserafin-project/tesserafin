using System;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// PR115c: the observable outcome of the live-wiring decision <c>MediaInfoHelper.SetDeviceSpecificData</c>
/// makes for one request - whether the response was actually served from the v2
/// <see cref="Tesserafin.Playback.Execution.PlaybackExecutionPlan"/>, or why it fell back to the legacy
/// <c>StreamInfo</c> instead. Retained per session (last decision wins, mirroring
/// <see cref="V2PlanRecord"/>'s own per-session retention) so the admin diagnostics surface can show
/// it - see <see cref="IPlaybackLiveWiringDiagnosticsStore"/>.
/// </summary>
/// <param name="ServedByV2">
/// <see langword="true"/> when the response was built by <see cref="Tesserafin.Playback.Dlna.PlaybackExecutionPlanAdapter.ToStreamInfo"/>
/// from the v2 plan; <see langword="false"/> when legacy's own <c>StreamInfo</c> was served instead.
/// </param>
/// <param name="FallbackReason">
/// Why legacy was served instead of v2, or <see langword="null"/> when <paramref name="ServedByV2"/>
/// is <see langword="true"/> - a served-by-v2 outcome never carries a fallback reason.
/// </param>
/// <param name="DecidedAt">When this decision was made.</param>
public sealed record PlaybackLiveWiringOutcome(
    bool ServedByV2,
    PlaybackLiveFallbackReason? FallbackReason,
    DateTimeOffset DecidedAt)
{
    /// <summary>
    /// Builds the outcome for a request actually served from the v2 plan.
    /// </summary>
    /// <param name="decidedAt">When this decision was made.</param>
    /// <returns>A served-by-v2 outcome, carrying no fallback reason.</returns>
    public static PlaybackLiveWiringOutcome Served(DateTimeOffset decidedAt) => new(true, null, decidedAt);

    /// <summary>
    /// Builds the outcome for a request that fell back to legacy.
    /// </summary>
    /// <param name="reason">Why legacy was served instead of v2.</param>
    /// <param name="decidedAt">When this decision was made.</param>
    /// <returns>A fallback outcome, carrying the typed reason.</returns>
    public static PlaybackLiveWiringOutcome Fallback(PlaybackLiveFallbackReason reason, DateTimeOffset decidedAt) => new(false, reason, decidedAt);
}
