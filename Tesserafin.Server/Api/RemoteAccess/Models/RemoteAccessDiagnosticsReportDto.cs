using System;
using System.Collections.Generic;

namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// Everything the server can observe about its own remote-access posture, and nothing it cannot.
/// </summary>
/// <remarks>
/// THERE IS NO OVERALL VERDICT HERE, AND THERE MUST NEVER BE ONE. No ready, secure, healthy,
/// reachable, working, available or can-publish field; no percentage; no score; no
/// recommendation. The affordance is the risk: a caller handed one boolean will act on the
/// boolean and never read the findings, and every summary word available would be a claim this
/// layer cannot support from inside the host.
///
/// Four findings are present in EVERY report and the API layer may not suppress or soften them —
/// external reachability, firewall state, router mapping and certificate readiness are unknowable
/// from this vantage point, and saying so is the honest answer rather than a gap to be filled.
/// </remarks>
public sealed class RemoteAccessDiagnosticsReportDto
{
    /// <summary>Gets or sets the version of this report's shape.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets when collection happened.</summary>
    public DateTimeOffset CollectedAt { get; set; }

    /// <summary>Gets or sets the question, as the server understood it.</summary>
    public RemoteAccessPolicyInputDto Input { get; set; } = new();

    /// <summary>Gets or sets the server's own listening posture.</summary>
    public RemoteAccessBackendObservationDto Backend { get; set; } = new();

    /// <summary>Gets or sets the forwarded-header and known-proxy agreement.</summary>
    public RemoteAccessProxyTrustObservationDto ProxyTrust { get; set; } = new();

    /// <summary>Gets or sets the census of the host's own addresses.</summary>
    public RemoteAccessLocalAddressCensusDto LocalAddresses { get; set; } = new();

    /// <summary>Gets or sets what was observed on the ingress ports.</summary>
    public IReadOnlyList<RemoteAccessListenerObservationDto> Listeners { get; set; } = Array.Empty<RemoteAccessListenerObservationDto>();

    /// <summary>Gets or sets what resolving the proposed hostname produced.</summary>
    public RemoteAccessDnsObservationDto Dns { get; set; } = new();

    /// <summary>
    /// Gets or sets the findings, in the engine's stable order.
    /// </summary>
    /// <remarks>
    /// Order is part of the contract. The engine emits findings in a fixed sequence so that two
    /// reports of the same posture are comparable, and the projection preserves it exactly rather
    /// than sorting, grouping or de-duplicating.
    /// </remarks>
    public IReadOnlyList<RemoteAccessFindingDto> Findings { get; set; } = Array.Empty<RemoteAccessFindingDto>();
}
