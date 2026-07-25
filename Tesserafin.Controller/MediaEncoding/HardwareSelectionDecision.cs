using System.Collections.Immutable;
using System.Linq;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The conclusive result of startup hardware selection: what the server will actually encode with
/// for this run, why, and what it looked at to get there. Produced once per start by
/// <see cref="HardwareSelectionPlanner"/> and logged as named structured fields, so "which encoder
/// is this container using and why" is answerable from the log alone.
/// </summary>
/// <param name="Mode">Whether the effective choice is hardware or software.</param>
/// <param name="Backend">The effective backend. Always <see cref="HardwareAccelerationType.none"/> when <paramref name="Mode"/> is <see cref="HardwareSelectionMode.Software"/>.</param>
/// <param name="Reason">Which rule produced this outcome.</param>
/// <param name="ConfiguredBackend">The backend configured as preferred before this start's probing. <see cref="HardwareAccelerationType.none"/> means "no preference - select automatically".</param>
/// <param name="CandidatesConsidered">Every catalog candidate that was applicable on this host, in the order they were considered.</param>
/// <param name="ProbeAttempts">Every candidate whose real trial encode was actually run, in order, with its outcome.</param>
public sealed record HardwareSelectionDecision(
    HardwareSelectionMode Mode,
    HardwareAccelerationType Backend,
    HardwareSelectionReason Reason,
    HardwareAccelerationType ConfiguredBackend,
    ImmutableArray<HardwareAccelerationType> CandidatesConsidered,
    ImmutableArray<HardwareProbeAttempt> ProbeAttempts)
{
    /// <summary>
    /// Gets the mode as the lower-case token used in the structured startup log
    /// (<c>hardware</c> / <c>software</c>). Kept deliberately lower-case to match
    /// <see cref="Backend"/>, whose enum members are already lower-case, so the two fields read
    /// consistently and can be matched with one convention.
    /// </summary>
    public string ModeName => Mode == HardwareSelectionMode.Hardware ? "hardware" : "software";

    /// <summary>
    /// Gets the backends that were actually probed on this start, in order.
    /// </summary>
    public ImmutableArray<HardwareAccelerationType> CandidatesProbed
        => [.. ProbeAttempts.Select(a => a.Backend)];

    /// <summary>
    /// Gets the distinct failure categories observed while probing, for the structured log. Empty
    /// when nothing was probed or everything succeeded.
    /// </summary>
    public ImmutableArray<FfmpegErrorCategory> ProbeFailureCategories
        => [.. ProbeAttempts.Where(a => !a.Outcome.Succeeded).Select(a => a.Outcome.FailureCategory).Distinct()];

    /// <summary>
    /// Creates the decision for a run where hardware encoding is switched off in configuration.
    /// </summary>
    /// <param name="configuredBackend">The persisted preferred backend, preserved unchanged in the record.</param>
    /// <returns>A software decision with no probes.</returns>
    public static HardwareSelectionDecision Disabled(HardwareAccelerationType configuredBackend)
        => new(
            HardwareSelectionMode.Software,
            HardwareAccelerationType.none,
            HardwareSelectionReason.HardwareDisabled,
            configuredBackend,
            [],
            []);
}
