using System;
using System.Collections.Generic;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Picks the first hardware acceleration backend, in priority order, that is applicable and passes
/// a real trial-encode probe (transcoding-pipeline plan PR10, generalizing PR8's VAAPI-only
/// startup probe to every backend). This is the invariant that keeps auto-selection safe: a
/// candidate is only ever returned after the probe delegate confirms it actually works, so a
/// wrong guess about a backend's applicability or trial-encode syntax can only ever produce
/// "not selected", never "selected and broken" - the same rule PR8 established for VAAPI alone.
/// </summary>
/// <remarks>
/// This is the narrow "which backend" view of the startup decision. Production uses
/// <see cref="HardwareSelectionPlanner"/> directly, because it also needs the reason, the
/// candidates considered and the per-candidate failure categories for the structured startup log.
/// This type delegates to the planner rather than reimplementing the walk, so the two can never
/// disagree about which candidate wins.
/// </remarks>
public static class HardwareBackendSelector
{
    /// <summary>
    /// Selects the first applicable, probe-verified candidate.
    /// </summary>
    /// <param name="candidatesInPriorityOrder">Candidates to consider, most preferred first.</param>
    /// <param name="options">The current encoding options.</param>
    /// <param name="ffmpegCapabilities">What the ffmpeg build supports.</param>
    /// <param name="probe">
    /// Runs a real trial encode for a candidate's built arguments and returns whether it
    /// succeeded. Production passes a closure over the real probe process; tests pass a stub over
    /// synthetic outcomes - this is what keeps the selection logic itself pure and testable
    /// without spawning ffmpeg.
    /// </param>
    /// <returns>The selected backend, or <c>null</c> if none of the candidates were applicable and verified.</returns>
    public static HardwareAccelerationType? SelectFirstVerified(
        IEnumerable<HardwareBackendCandidate> candidatesInPriorityOrder,
        EncodingOptions options,
        FfmpegBuildCapabilities ffmpegCapabilities,
        Func<HardwareBackendCandidate, string, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var decision = HardwareSelectionPlanner.Decide(
            candidatesInPriorityOrder,
            options,
            ffmpegCapabilities,
            (candidate, arguments) => probe(candidate, arguments)
                ? HardwareProbeOutcome.Success
                : HardwareProbeOutcome.Failure(FfmpegErrorCategory.Unknown));

        return decision.Mode == HardwareSelectionMode.Hardware ? decision.Backend : null;
    }
}
