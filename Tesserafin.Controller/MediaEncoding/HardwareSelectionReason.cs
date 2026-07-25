namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Why <see cref="HardwareSelectionPlanner"/> ended up with the effective backend it did. This is a
/// bounded set on purpose: the startup decision is a diagnosable contract, not a prose message, so
/// operators and the container acceptance gates can key on it exactly.
/// </summary>
public enum HardwareSelectionReason
{
    /// <summary>
    /// Hardware encoding is switched off in configuration
    /// (<c>EnableHardwareEncoding=false</c>). No probes were run and none will be: this is the
    /// documented way to force software transcoding.
    /// </summary>
    HardwareDisabled,

    /// <summary>
    /// The backend configured as preferred was probed on this start and its real trial encode
    /// succeeded, so it became the effective backend.
    /// </summary>
    PreferredBackendVerified,

    /// <summary>
    /// No preferred backend was configured, or the preferred one failed its probe, and a later
    /// catalog candidate passed its real trial encode instead.
    /// </summary>
    AutoSelectedBackendVerified,

    /// <summary>
    /// Hardware encoding is enabled, but no catalog candidate was even applicable on this host, so
    /// nothing was probed. The usual cause is that no device node or build support is present -
    /// for example a container started with no <c>/dev/dri</c> mapping.
    /// </summary>
    NoApplicableBackend,

    /// <summary>
    /// Hardware encoding is enabled and at least one candidate was applicable and probed, but every
    /// probe failed. Software is the effective choice.
    /// </summary>
    AllProbesFailed,
}
