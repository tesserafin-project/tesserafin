using System.Net;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// A local address and what kind of address it is.
/// </summary>
/// <param name="Address">The address, with any IPv4-mapped IPv6 form already normalized away.</param>
/// <param name="Class">Its topology classification.</param>
public sealed record ClassifiedAddress(IPAddress Address, AddressClass Class);
