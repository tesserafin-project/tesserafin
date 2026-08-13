using System.Collections.Generic;
using System.Net;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Supplies the host's own unicast addresses.
/// </summary>
/// <remarks>
/// Narrow on purpose. It yields addresses and nothing else — no interface names, no MAC
/// addresses, no gateways, no DNS suffixes — so there is no route by which unrelated machine
/// identity could reach a diagnostic report.
/// </remarks>
public interface ILocalAddressSource
{
    /// <summary>
    /// Gets the operational unicast addresses of this host.
    /// </summary>
    /// <returns>The addresses. Duplicates and ordering are the caller's problem to normalize.</returns>
    IReadOnlyList<IPAddress> GetUnicastAddresses();
}
