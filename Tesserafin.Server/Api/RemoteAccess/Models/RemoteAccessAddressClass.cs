namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>What kind of address this is. Mirrors <c>AddressClass</c>.</summary>
public enum RemoteAccessAddressClass
{
    /// <summary>Reserved.</summary>
    None = 0,

    /// <summary>Loopback.</summary>
    Loopback = 1,

    /// <summary>Link-local.</summary>
    LinkLocal = 2,

    /// <summary>Private (RFC 1918 and equivalents).</summary>
    Private = 3,

    /// <summary>Shared address space (RFC 6598).</summary>
    SharedAddressSpace = 4,

    /// <summary>Globally routable.</summary>
    GloballyRoutable = 5,

    /// <summary>Multicast.</summary>
    Multicast = 6,

    /// <summary>Unspecified.</summary>
    Unspecified = 7,

    /// <summary>Anything else.</summary>
    Other = 8
}
