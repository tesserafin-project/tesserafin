namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// A census of the host's own addresses, by class.
/// </summary>
/// <remarks>
/// A census rather than a list, for the same reason as the proxy counts. Every topology finding —
/// private addressing, shared address space, direct public possible, NAT likely, CG-NAT signal —
/// is decided by WHICH CLASSES are present and how many, never by a particular address. Emitting
/// the addresses themselves would put the host's public IP into a document operators paste into
/// issue trackers, and would not explain one additional finding.
/// </remarks>
public sealed class RemoteAccessLocalAddressCensusDto
{
    /// <summary>Gets or sets how many loopback addresses were observed.</summary>
    public int LoopbackCount { get; set; }

    /// <summary>Gets or sets how many link-local addresses were observed.</summary>
    public int LinkLocalCount { get; set; }

    /// <summary>Gets or sets how many private addresses were observed.</summary>
    public int PrivateCount { get; set; }

    /// <summary>Gets or sets how many shared-address-space (RFC 6598) addresses were observed.</summary>
    public int SharedAddressSpaceCount { get; set; }

    /// <summary>Gets or sets how many globally routable addresses were observed.</summary>
    public int GloballyRoutableCount { get; set; }

    /// <summary>Gets or sets how many multicast addresses were observed.</summary>
    public int MulticastCount { get; set; }

    /// <summary>Gets or sets how many unspecified addresses were observed.</summary>
    public int UnspecifiedCount { get; set; }

    /// <summary>Gets or sets how many addresses fell into no other class.</summary>
    public int OtherCount { get; set; }
}
