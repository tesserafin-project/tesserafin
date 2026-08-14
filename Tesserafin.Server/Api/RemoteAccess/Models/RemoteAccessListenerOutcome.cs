namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>What inspecting a port produced. Mirrors <c>ListenerObservationOutcome</c>.</summary>
public enum RemoteAccessListenerOutcome
{
    /// <summary>Reserved.</summary>
    None = 0,

    /// <summary>A listener was observed.</summary>
    ObservedListener = 1,

    /// <summary>No listener was observed.</summary>
    NoListenerObserved = 2,

    /// <summary>The operating system denied the inspection.</summary>
    InspectionDenied = 3,

    /// <summary>Inspection is unsupported on this platform.</summary>
    Unsupported = 4,

    /// <summary>Inspection failed for another reason.</summary>
    Unknown = 5
}
