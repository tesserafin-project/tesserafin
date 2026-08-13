using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Extensions;
using Tesserafin.Model.Net;

namespace Tesserafin.Server.Net;

/// <summary>
/// Secure bootstrap mode: the one place that decides whether the server is confined to loopback,
/// and the one place that turns a derived bind set into that confinement.
/// </summary>
/// <remarks>
/// <para>
/// The mode exists so that a server on a host with a public address can be brought up, claimed and
/// configured without its unfinished setup wizard ever being reachable from outside. It is a
/// binding constraint and nothing more: it opens no port, publishes nothing, and is not evidence
/// that public access is safe. See tesserafin-project/tesserafin#241 §1.4 and §5.
/// </para>
/// <para>
/// Both listeners the server ever opens — the pre-startup setup server and the main Kestrel host —
/// reach Kestrel through <c>Extensions.WebHostBuilderExtensions.SetupTesserafinWebServer</c>, and
/// that method applies <see cref="ConstrainToLoopback"/> once, before it opens anything. There is
/// deliberately no second copy of this decision: a mode that the setup server and the main server
/// could disagree about would be worse than no mode at all.
/// </para>
/// </remarks>
public static class SecureBootstrap
{
    /// <summary>
    /// Gets a value indicating whether secure bootstrap mode is active for this process.
    /// </summary>
    /// <remarks>
    /// Delegates to <c>ConfigurationExtensions.UseSecureBootstrap</c>, which reads
    /// <see cref="Tesserafin.Controller.Extensions.ConfigurationExtensions.SecureBootstrapKey"/>. This wrapper exists so the binding
    /// path has one symbol to depend on, and so a null configuration fails closed to the ordinary
    /// behaviour rather than throwing during startup.
    /// </remarks>
    /// <param name="startupConfig">The startup configuration, built before any socket is bound.</param>
    /// <returns><c>true</c> if every listener must be confined to loopback.</returns>
    public static bool IsEnabled(IConfiguration? startupConfig)
        => startupConfig is not null && startupConfig.UseSecureBootstrap();

    /// <summary>
    /// Replaces a derived bind set with the loopback addresses that cover the same address families.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The input is whatever <c>NetworkManager.GetAllBindInterfaces</c> produced, which is one of
    /// three shapes: a single <see cref="IPAddress.IPv6Any"/> entry when both families are enabled
    /// (Kestrel binds that socket dual-mode, so it covers IPv4 as well), a single
    /// <see cref="IPAddress.Any"/> entry when only IPv4 is enabled, or a list of concrete interface
    /// addresses. The wildcard entries are therefore expanded by what they actually mean rather
    /// than by their own address family — an <c>IPv6Any</c> entry yields BOTH <c>127.0.0.1</c> and
    /// <c>::1</c>, because reducing it to <c>::1</c> alone would break an operator forwarding to
    /// <c>127.0.0.1</c> over SSH, which is the documented bootstrap path.
    /// </para>
    /// <para>
    /// An empty input yields an empty result. Secure bootstrap mode narrows what the server would
    /// otherwise have bound; it never adds a listener the ordinary derivation would not have
    /// produced.
    /// </para>
    /// </remarks>
    /// <param name="addresses">The bind set derived from the network configuration.</param>
    /// <returns>Loopback-only bind addresses. Never a wildcard, a LAN address or a public address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="addresses"/> is <c>null</c>.</exception>
    public static IReadOnlyList<IPData> ConstrainToLoopback(IReadOnlyList<IPData> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var wantIPv4 = false;
        var wantIPv6 = false;

        foreach (var entry in addresses)
        {
            var address = entry.Address;

            if (address.Equals(IPAddress.IPv6Any))
            {
                // Dual-mode wildcard: it is serving both stacks, so both loopbacks replace it.
                wantIPv4 = true;
                wantIPv6 = true;
                continue;
            }

            if (address.Equals(IPAddress.Any))
            {
                wantIPv4 = true;
                continue;
            }

            switch (entry.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    wantIPv4 = true;
                    break;
                case AddressFamily.InterNetworkV6:
                    wantIPv6 = true;
                    break;
                default:
                    break;
            }
        }

        var loopbacks = new List<IPData>();
        if (wantIPv4)
        {
            loopbacks.Add(new IPData(IPAddress.Loopback, NetworkConstants.IPv4RFC5735Loopback, "lo"));
        }

        if (wantIPv6)
        {
            loopbacks.Add(new IPData(IPAddress.IPv6Loopback, NetworkConstants.IPv6RFC4291Loopback, "lo"));
        }

        return loopbacks;
    }
}
