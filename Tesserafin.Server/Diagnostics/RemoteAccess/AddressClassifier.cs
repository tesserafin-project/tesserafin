using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Decides what kind of address an address is, and puts a set of them into a stable order.
/// </summary>
/// <remarks>
/// Pure and allocation-cheap; it asks the address itself and nothing else. In particular it does
/// not consult the server's LAN configuration: <c>LocalNetworkSubnets</c> can redefine what the
/// application treats as local, which is the right answer for authorization and the wrong answer
/// for topology. A diagnostic that inherited that redefinition could be configured into reporting
/// a public address as private.
/// </remarks>
public static class AddressClassifier
{
    private static readonly byte[] _sharedAddressSpaceSecondOctetRange = { 64, 127 };

    /// <summary>
    /// Classifies one address.
    /// </summary>
    /// <param name="address">The address to classify. IPv4-mapped IPv6 is normalized first.</param>
    /// <returns>Its classification.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <c>null</c>.</exception>
    public static AddressClass Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var normalized = Normalize(address);

        if (IPAddress.IsLoopback(normalized))
        {
            return AddressClass.Loopback;
        }

        if (normalized.Equals(IPAddress.Any) || normalized.Equals(IPAddress.IPv6Any))
        {
            return AddressClass.Unspecified;
        }

        if (normalized.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ClassifyIPv6(normalized);
        }

        return ClassifyIPv4(normalized);
    }

    /// <summary>
    /// Normalizes an IPv4-mapped IPv6 address back to its IPv4 form.
    /// </summary>
    /// <remarks>
    /// Kestrel in dual-socket mode and several operating-system listings report the same address
    /// in both shapes. Without this, one host address would be classified twice and could be
    /// counted as both an IPv4 and an IPv6 fact.
    /// </remarks>
    /// <param name="address">The address to normalize.</param>
    /// <returns>The normalized address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <c>null</c>.</exception>
    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>
    /// Normalizes, classifies, deduplicates and stably orders a set of addresses.
    /// </summary>
    /// <remarks>
    /// The ordering is by address family, then by the address bytes, so two collections of the
    /// same host addresses always produce the same report. Interfaces appearing and disappearing
    /// between collections must not change the answer's shape.
    /// </remarks>
    /// <param name="addresses">The addresses to process. <c>null</c> entries are skipped.</param>
    /// <returns>The classified set, deduplicated and ordered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="addresses"/> is <c>null</c>.</exception>
    public static IReadOnlyList<ClassifiedAddress> ClassifySet(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ClassifiedAddress>();

        foreach (var address in addresses)
        {
            if (address is null)
            {
                continue;
            }

            var normalized = Normalize(address);
            if (!seen.Add(normalized.ToString()))
            {
                continue;
            }

            result.Add(new ClassifiedAddress(normalized, Classify(normalized)));
        }

        result.Sort(CompareForStableOrder);
        return result;
    }

    private static int CompareForStableOrder(ClassifiedAddress left, ClassifiedAddress right)
    {
        var byFamily = ((int)left.Address.AddressFamily).CompareTo((int)right.Address.AddressFamily);
        if (byFamily != 0)
        {
            return byFamily;
        }

        var leftBytes = left.Address.GetAddressBytes();
        var rightBytes = right.Address.GetAddressBytes();
        var shared = Math.Min(leftBytes.Length, rightBytes.Length);

        for (var i = 0; i < shared; i++)
        {
            if (leftBytes[i] != rightBytes[i])
            {
                return leftBytes[i].CompareTo(rightBytes[i]);
            }
        }

        return leftBytes.Length.CompareTo(rightBytes.Length);
    }

    private static AddressClass ClassifyIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        // 224.0.0.0/4.
        if (bytes[0] >= 224 && bytes[0] <= 239)
        {
            return AddressClass.Multicast;
        }

        // 169.254.0.0/16.
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return AddressClass.LinkLocal;
        }

        // RFC 6598 shared address space, 100.64.0.0/10. Checked BEFORE the RFC 1918 ranges and
        // kept as its own class: it is the only locally visible signal of carrier-grade NAT, and
        // folding it into Private would destroy the evidence.
        if (bytes[0] == 100
            && bytes[1] >= _sharedAddressSpaceSecondOctetRange[0]
            && bytes[1] <= _sharedAddressSpaceSecondOctetRange[1])
        {
            return AddressClass.SharedAddressSpace;
        }

        // RFC 1918.
        if (bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168))
        {
            return AddressClass.Private;
        }

        // 0.0.0.0/8, 240.0.0.0/4 and the broadcast address are none of the above and are not
        // routable either.
        if (bytes[0] == 0 || bytes[0] >= 240)
        {
            return AddressClass.Other;
        }

        return AddressClass.GloballyRoutable;
    }

    private static AddressClass ClassifyIPv6(IPAddress address)
    {
        if (address.IsIPv6Multicast)
        {
            return AddressClass.Multicast;
        }

        if (address.IsIPv6LinkLocal)
        {
            return AddressClass.LinkLocal;
        }

        if (address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal)
        {
            return AddressClass.Private;
        }

        var bytes = address.GetAddressBytes();

        // 2000::/3 is the only currently allocated global unicast range. Anything outside it is
        // reserved or special-purpose, and calling it globally routable would be a guess.
        if ((bytes[0] & 0xE0) == 0x20)
        {
            return AddressClass.GloballyRoutable;
        }

        return AddressClass.Other;
    }
}
