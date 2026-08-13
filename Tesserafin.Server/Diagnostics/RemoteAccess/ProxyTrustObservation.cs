using System.Collections.Generic;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// The configured trust boundary between the server and whatever sits in front of it.
/// </summary>
/// <param name="ConfiguredKnownProxies">The known-proxy entries exactly as configured.</param>
/// <param name="ParsedKnownProxyCount">How many of them the server's own parser accepted.</param>
/// <param name="ForwardedHeadersEnabled">Whether forwarded-header processing is on as a result.</param>
public sealed record ProxyTrustObservation(
    IReadOnlyList<string> ConfiguredKnownProxies,
    int ParsedKnownProxyCount,
    bool ForwardedHeadersEnabled);
