namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// The publication policy, echoed back as the server understood it.
/// </summary>
/// <remarks>
/// Echoed on purpose. A caller reading a report needs to know which question was answered, and
/// "the hostname I sent" and "the hostname the server normalised and used" are not always the
/// same string. Everything here is caller-supplied; nothing is observed.
/// </remarks>
public sealed class RemoteAccessPolicyInputDto
{
    /// <summary>Gets or sets the hostname as the server understood it, or null if none was usable.</summary>
    public string? Hostname { get; set; }

    /// <summary>Gets or sets the IPv4 intention the server understood.</summary>
    public RemoteAccessPublicationPolicy IPv4Policy { get; set; }

    /// <summary>Gets or sets the IPv6 intention the server understood.</summary>
    public RemoteAccessPublicationPolicy IPv6Policy { get; set; }
}
