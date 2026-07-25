using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Decides, once per server start, whether this run transcodes in hardware or software - and if in
/// hardware, with which backend.
/// </summary>
/// <remarks>
/// The contract this implements, and the reasoning behind each rule:
///
/// <list type="bullet">
/// <item>
/// <description>
/// <c>EnableHardwareEncoding=false</c> is the documented way to force software. It short-circuits
/// before any probe runs, so it costs no startup latency and never spins a GPU. The persisted
/// preferred backend is left untouched, so switching hardware encoding back on restores the
/// operator's previous choice as the first candidate.
/// </description>
/// </item>
/// <item>
/// <description>
/// A configured non-<see cref="HardwareAccelerationType.none"/> backend is a <em>preference</em>,
/// never proof. It is considered first, but it only becomes effective after its own real trial
/// encode succeeds on this host, on this start. If it fails, the remaining catalog candidates are
/// still tried rather than dropping straight to software - a host whose preferred accelerator broke
/// should still use one that works.
/// </description>
/// </item>
/// <item>
/// <description>
/// Probing runs on <em>every</em> start, not only on a fresh install. This is the property that
/// makes a persisted selection safe to carry between hosts: a config directory moved from a GPU
/// machine to one with no render node cannot produce an unprobed hardware command, because the
/// backend it names is re-verified before anything is assembled from it. It is also what makes the
/// GPU-host-to-no-GPU-host migration end in software rather than a broken transcode.
/// </description>
/// </item>
/// <item>
/// <description>
/// When nothing is verified the effective backend is <see cref="HardwareAccelerationType.none"/> and
/// the mode is <see cref="HardwareSelectionMode.Software"/>. There is no third outcome and no
/// failure path: boot always continues. The two software reasons are kept apart on purpose -
/// <see cref="HardwareSelectionReason.NoApplicableBackend"/> (nothing was even worth probing, the
/// normal no-device container case) diagnoses very differently from
/// <see cref="HardwareSelectionReason.AllProbesFailed"/> (a device is present but unusable).
/// </description>
/// </item>
/// </list>
///
/// The planner is pure: it spawns nothing and touches no configuration. Running the trial encode is
/// the caller's <c>probe</c> delegate, which is what lets the whole decision table be tested against
/// synthetic outcomes without ffmpeg, while production passes a closure over the real
/// <c>HardwareBackendProbe</c>.
/// </remarks>
public static class HardwareSelectionPlanner
{
    /// <summary>
    /// Produces this start's hardware selection decision.
    /// </summary>
    /// <param name="candidatesInPriorityOrder">The catalog, most preferred first.</param>
    /// <param name="options">The current encoding options, read but never mutated.</param>
    /// <param name="ffmpegCapabilities">What the ffmpeg build supports.</param>
    /// <param name="probe">
    /// Runs a real trial encode for a candidate's built argument line and reports what happened. A
    /// delegate that throws is treated as a failed probe, never as a fatal startup error: a broken
    /// probe must not be able to take the server down or, worse, leave a backend selected without
    /// verification.
    /// </param>
    /// <returns>The conclusive decision for this run.</returns>
    public static HardwareSelectionDecision Decide(
        IEnumerable<HardwareBackendCandidate> candidatesInPriorityOrder,
        EncodingOptions options,
        FfmpegBuildCapabilities ffmpegCapabilities,
        Func<HardwareBackendCandidate, string, HardwareProbeOutcome> probe)
    {
        ArgumentNullException.ThrowIfNull(candidatesInPriorityOrder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);

        var configuredBackend = options.HardwareAccelerationType;

        if (!options.EnableHardwareEncoding)
        {
            return HardwareSelectionDecision.Disabled(configuredBackend);
        }

        var considered = ImmutableArray.CreateBuilder<HardwareAccelerationType>();
        var attempts = ImmutableArray.CreateBuilder<HardwareProbeAttempt>();

        foreach (var candidate in OrderByPreference(candidatesInPriorityOrder, configuredBackend))
        {
            if (!candidate.IsApplicable(options, ffmpegCapabilities))
            {
                continue;
            }

            // A candidate that cannot even build an argument line for these options (no usable
            // device path, say) was never really applicable, so it is not counted as considered.
            var arguments = candidate.BuildTrialEncodeArguments(options);
            if (string.IsNullOrEmpty(arguments))
            {
                continue;
            }

            considered.Add(candidate.Type);

            HardwareProbeOutcome outcome;
            try
            {
                outcome = probe(candidate, arguments) ?? HardwareProbeOutcome.Failure(FfmpegErrorCategory.Unknown);
            }
            catch (Exception)
            {
                // Contained deliberately. The caller logs the exception; here it can only ever mean
                // "this backend is not verified", which is the safe direction.
                outcome = HardwareProbeOutcome.Failure(FfmpegErrorCategory.Unknown);
            }

            attempts.Add(new HardwareProbeAttempt(candidate.Type, outcome));

            if (outcome.Succeeded)
            {
                return new HardwareSelectionDecision(
                    HardwareSelectionMode.Hardware,
                    candidate.Type,
                    candidate.Type == configuredBackend
                        ? HardwareSelectionReason.PreferredBackendVerified
                        : HardwareSelectionReason.AutoSelectedBackendVerified,
                    configuredBackend,
                    considered.ToImmutable(),
                    attempts.ToImmutable());
            }
        }

        return new HardwareSelectionDecision(
            HardwareSelectionMode.Software,
            HardwareAccelerationType.none,
            attempts.Count == 0
                ? HardwareSelectionReason.NoApplicableBackend
                : HardwareSelectionReason.AllProbesFailed,
            configuredBackend,
            considered.ToImmutable(),
            attempts.ToImmutable());
    }

    /// <summary>
    /// Moves the configured backend to the front without otherwise disturbing the catalog's
    /// priority order. The catalog order itself is left exactly as it is: reordering it would need
    /// measured throughput evidence across backends this environment cannot produce.
    /// </summary>
    private static IEnumerable<HardwareBackendCandidate> OrderByPreference(
        IEnumerable<HardwareBackendCandidate> candidatesInPriorityOrder,
        HardwareAccelerationType configuredBackend)
    {
        if (configuredBackend == HardwareAccelerationType.none)
        {
            return candidatesInPriorityOrder;
        }

        var candidates = candidatesInPriorityOrder as IReadOnlyList<HardwareBackendCandidate> ?? [.. candidatesInPriorityOrder];
        var preferred = candidates.Where(c => c.Type == configuredBackend);
        var rest = candidates.Where(c => c.Type != configuredBackend);
        return preferred.Concat(rest);
    }
}
