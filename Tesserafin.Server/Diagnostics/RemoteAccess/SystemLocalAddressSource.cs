using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Reads this host's unicast addresses from the operating system.
/// </summary>
/// <remarks>
/// Enumeration is racy by nature — an interface can vanish between the listing and the read of
/// its properties — so every per-interface failure is skipped rather than propagated. A
/// diagnostic that throws because a VPN adapter went down mid-collection is a diagnostic nobody
/// can run while troubleshooting a VPN.
/// </remarks>
public sealed class SystemLocalAddressSource : ILocalAddressSource
{
    /// <inheritdoc />
    public IReadOnlyList<IPAddress> GetUnicastAddresses()
    {
        var addresses = new List<IPAddress>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }

        foreach (var networkInterface in interfaces)
        {
            try
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    addresses.Add(unicast.Address);
                }
            }
            catch (NetworkInformationException)
            {
                // The interface went away between the listing and the read.
            }
            catch (PlatformNotSupportedException)
            {
                // Some properties are unavailable on some platforms; the rest still count.
            }
        }

        return addresses;
    }
}
