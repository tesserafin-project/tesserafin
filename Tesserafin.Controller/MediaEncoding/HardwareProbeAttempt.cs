using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// One entry in the startup decision's audit trail: a candidate that was actually probed, and what
/// its real trial encode did.
/// </summary>
/// <param name="Backend">The backend that was probed.</param>
/// <param name="Outcome">What its trial encode did.</param>
public sealed record HardwareProbeAttempt(
    HardwareAccelerationType Backend,
    HardwareProbeOutcome Outcome);
