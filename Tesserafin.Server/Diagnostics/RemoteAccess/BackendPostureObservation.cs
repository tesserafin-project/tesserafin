namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// What the server's own listeners are configured to do.
/// </summary>
/// <remarks>
/// Derived from the same production code the server uses at startup — the network manager's bind
/// derivation and, when the mode is on, the secure-bootstrap loopback constraint. A second
/// interpretation of bind semantics would be a diagnostic that agrees with itself and disagrees
/// with the server.
/// </remarks>
/// <param name="SecureBootstrapActive">Whether secure bootstrap mode is on for this process.</param>
/// <param name="Posture">The derived bind posture.</param>
/// <param name="UnixSocketConfigured">Whether a Unix domain socket is configured.</param>
/// <param name="InternalHttpPort">The configured internal HTTP port.</param>
/// <param name="InternalHttpsPort">The configured internal HTTPS port.</param>
public sealed record BackendPostureObservation(
    bool SecureBootstrapActive,
    BackendBindPosture Posture,
    bool UnixSocketConfigured,
    int InternalHttpPort,
    int InternalHttpsPort);
