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
        foreach (var candidate in candidatesInPriorityOrder)
        {
            if (!candidate.IsApplicable(options, ffmpegCapabilities))
            {
                continue;
            }

            var arguments = candidate.BuildTrialEncodeArguments(options);
            if (arguments is null)
            {
                continue;
            }

            if (probe(candidate, arguments))
            {
                return candidate.Type;
            }
        }

        return null;
    }
}
