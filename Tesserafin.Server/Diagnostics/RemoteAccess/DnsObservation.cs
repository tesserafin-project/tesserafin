using System.Collections.Generic;
using System.Net;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// The result of resolving the proposed hostname, and nothing more.
/// </summary>
/// <remarks>
/// The addresses here are answers, not destinations. Nothing in this layer connects to them; see
/// the structural gate that enforces it.
/// </remarks>
/// <param name="NormalizedHostname">The hostname after IDN normalization, or <c>null</c> if none was usable.</param>
/// <param name="Outcome">How the lookup ended.</param>
/// <param name="Addresses">Deduplicated, stably ordered A and AAAA answers.</param>
public sealed record DnsObservation(
    string? NormalizedHostname,
    DnsLookupOutcome Outcome,
    IReadOnlyList<IPAddress> Addresses);
