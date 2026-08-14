namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>How the backend's listening addresses are constrained. Mirrors <c>BackendBindPosture</c>.</summary>
public enum RemoteAccessBackendBindPosture
{
    /// <summary>Reserved.</summary>
    None = 0,

    /// <summary>Loopback only.</summary>
    LoopbackOnly = 1,

    /// <summary>A wildcard address.</summary>
    Wildcard = 2,

    /// <summary>Explicit private addresses.</summary>
    ExplicitPrivateAddresses = 3,

    /// <summary>Could not be determined.</summary>
    Unknown = 4,

    /// <summary>Explicit globally routable addresses.</summary>
    ExplicitGloballyRoutableAddresses = 5
}
