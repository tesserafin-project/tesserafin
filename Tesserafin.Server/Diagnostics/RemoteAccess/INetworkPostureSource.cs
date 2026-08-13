namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Reads the server's own effective networking configuration, without touching it.
/// </summary>
public interface INetworkPostureSource
{
    /// <summary>
    /// Gets the current backend listener posture.
    /// </summary>
    /// <returns>The observation.</returns>
    BackendPostureObservation GetBackendPosture();

    /// <summary>
    /// Gets the current proxy trust boundary.
    /// </summary>
    /// <returns>The observation.</returns>
    ProxyTrustObservation GetProxyTrust();
}
