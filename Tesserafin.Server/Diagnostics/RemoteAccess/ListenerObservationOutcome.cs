namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// What was learned about one TCP port.
/// </summary>
/// <remarks>
/// There is deliberately no <c>Available</c> and no <c>Free</c>. The absence of a listener in a
/// read-only listing says that nothing was listening at the instant of the read; it says nothing
/// about whether a future privileged service may bind, whether policy permits it, or whether
/// anything outside the host could reach it if it did.
/// </remarks>
public enum ListenerObservationOutcome
{
    /// <summary>Not an outcome. The default value of the type, never emitted.</summary>
    None = 0,

    /// <summary>At least one TCP listener was present on the port.</summary>
    ObservedListener = 1,

    /// <summary>The port did not appear in the listener table. Not a claim that it is bindable.</summary>
    NoListenerObserved = 2,

    /// <summary>The operating system refused the listing.</summary>
    InspectionDenied = 3,

    /// <summary>This platform offers no read-only way to list listeners.</summary>
    Unsupported = 4,

    /// <summary>The listing failed for a reason that is neither denial nor lack of support.</summary>
    Unknown = 5
}
