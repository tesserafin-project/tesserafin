namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// What kind of address a local interface address is, for topology purposes.
/// </summary>
/// <remarks>
/// <see cref="SharedAddressSpace"/> is the reason this enum is not just "private or not":
/// RFC 6598 space is the one locally observable signal that carrier-grade NAT may be in the
/// path, and conflating it with RFC 1918 would erase the only CGNAT evidence a host can see
/// without asking the Internet.
/// </remarks>
public enum AddressClass
{
    /// <summary>Not a classification. The default value of the type, never emitted.</summary>
    None = 0,

    /// <summary>127.0.0.0/8 or ::1.</summary>
    Loopback = 1,

    /// <summary>169.254.0.0/16 or fe80::/10.</summary>
    LinkLocal = 2,

    /// <summary>RFC 1918 space, or IPv6 unique-local fc00::/7.</summary>
    Private = 3,

    /// <summary>RFC 6598 shared address space, 100.64.0.0/10.</summary>
    SharedAddressSpace = 4,

    /// <summary>Routable on the public Internet, as far as the address itself says.</summary>
    GloballyRoutable = 5,

    /// <summary>A multicast address.</summary>
    Multicast = 6,

    /// <summary>0.0.0.0 or ::.</summary>
    Unspecified = 7,

    /// <summary>Reserved, documentation, or otherwise not one of the above.</summary>
    Other = 8
}
