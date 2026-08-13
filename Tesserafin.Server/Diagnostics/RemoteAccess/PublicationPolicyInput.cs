namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// What the caller says they intend to publish, so the report can disagree with it.
/// </summary>
/// <remarks>
/// The families are separate fields rather than one flag because they fail separately. An
/// operator who publishes IPv4, forgets the IPv6 firewall and is told "looks fine" has been
/// actively misled; see the address-family parity rule.
/// </remarks>
/// <param name="ProposedHostname">The hostname as supplied, before validation.</param>
/// <param name="PublishIPv4">Whether IPv4 is intended to be published; <c>null</c> if unstated.</param>
/// <param name="PublishIPv6">Whether IPv6 is intended to be published; <c>null</c> if unstated.</param>
public sealed record PublicationPolicyInput(string? ProposedHostname, bool? PublishIPv4, bool? PublishIPv6);
