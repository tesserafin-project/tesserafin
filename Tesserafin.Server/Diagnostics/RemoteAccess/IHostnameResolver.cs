using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Resolves a hostname to addresses, and does nothing with them.
/// </summary>
public interface IHostnameResolver
{
    /// <summary>
    /// Resolves an already-validated hostname.
    /// </summary>
    /// <remarks>
    /// The contract is a lookup, not a reachability test. An implementation may not open a socket
    /// to anything it resolves; a structural gate enforces that for the shipped implementation.
    /// </remarks>
    /// <param name="normalizedHostname">A hostname already accepted by <see cref="HostnameInput"/>.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The observation, including the distinct ways a lookup can fail to answer.</returns>
    Task<DnsObservation> ResolveAsync(string normalizedHostname, CancellationToken cancellationToken);
}
