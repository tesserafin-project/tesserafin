using System.Collections.Generic;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Reports what is listening on the two ports an ingress would use.
/// </summary>
public interface ITcpListenerSource
{
    /// <summary>
    /// Observes the given TCP ports.
    /// </summary>
    /// <remarks>
    /// Read-only in the strongest sense: an implementation may not bind, may not connect, and may
    /// not ask who owns a socket.
    /// </remarks>
    /// <param name="ports">The ports to examine.</param>
    /// <returns>One observation per requested port, in the order requested.</returns>
    IReadOnlyList<PortListenerObservation> Observe(IReadOnlyList<int> ports);
}
