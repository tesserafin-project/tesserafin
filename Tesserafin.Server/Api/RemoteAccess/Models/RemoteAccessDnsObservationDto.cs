namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// What resolving the proposed hostname produced.
/// </summary>
/// <remarks>
/// The normalised hostname is echoed because the caller supplied it and needs to see which string
/// was actually looked up. The resolved addresses are reduced to a count and two family flags:
/// that is everything the DNS findings are decided by.
/// </remarks>
public sealed class RemoteAccessDnsObservationDto
{
    /// <summary>Gets or sets the hostname as normalised, or null if none was usable.</summary>
    public string? NormalizedHostname { get; set; }

    /// <summary>Gets or sets what the lookup produced.</summary>
    public RemoteAccessDnsOutcome Outcome { get; set; }

    /// <summary>Gets or sets how many addresses were returned.</summary>
    public int AddressCount { get; set; }

    /// <summary>Gets or sets a value indicating whether any returned address is IPv4.</summary>
    public bool ContainsIPv4 { get; set; }

    /// <summary>Gets or sets a value indicating whether any returned address is IPv6.</summary>
    public bool ContainsIPv6 { get; set; }
}
