namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// What the server's own HTTP listeners are configured to accept on.
/// </summary>
public enum BackendBindPosture
{
    /// <summary>Not a posture. The default value of the type, never emitted.</summary>
    None = 0,

    /// <summary>Every derived bind address is loopback.</summary>
    LoopbackOnly = 1,

    /// <summary>At least one derived bind address is a wildcard, so every interface is accepted on.</summary>
    Wildcard = 2,

    /// <summary>Bound to explicit non-loopback addresses, none of them globally routable.</summary>
    ExplicitPrivateAddresses = 3,

    /// <summary>The bind set could not be derived.</summary>
    Unknown = 4,

    /// <summary>
    /// At least one explicit bind address is globally routable. Distinguished from
    /// <see cref="ExplicitPrivateAddresses"/> because the socket's own reach differs, and a
    /// diagnostic that reported both as "explicit" would conceal the more exposed of the two.
    /// </summary>
    ExplicitGloballyRoutableAddresses = 5
}
