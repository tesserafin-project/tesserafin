namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// Whether forwarded headers and the configured proxy list agree with each other.
/// </summary>
/// <remarks>
/// COUNTS, NOT VALUES, and that is deliberate. Every proxy-trust finding — absent, malformed,
/// multiple, single-and-normalised — is decided by how many entries were configured versus how
/// many parsed, so the counts explain the findings completely. The configured entries themselves
/// are addresses of other machines on the operator's network, they explain nothing further, and a
/// diagnostic report is a thing people paste into issues.
/// </remarks>
public sealed class RemoteAccessProxyTrustObservationDto
{
    /// <summary>Gets or sets how many known proxies are configured.</summary>
    public int ConfiguredKnownProxyCount { get; set; }

    /// <summary>Gets or sets how many of them parsed as addresses.</summary>
    public int ParsedKnownProxyCount { get; set; }

    /// <summary>Gets or sets a value indicating whether forwarded headers are enabled.</summary>
    public bool ForwardedHeadersEnabled { get; set; }
}
