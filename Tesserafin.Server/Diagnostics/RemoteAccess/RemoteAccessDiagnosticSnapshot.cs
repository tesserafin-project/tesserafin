using System;
using System.Collections.Generic;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Everything that was observed, frozen, before anything is concluded from it.
/// </summary>
/// <remarks>
/// The split between this record and <see cref="RemoteAccessDiagnosticEvaluator"/> is what makes
/// the classification rules testable without a network, a resolver, a privileged listing or a
/// clock. <see cref="CollectedAt"/> is stamped once at the collection boundary and carried here,
/// so no classification rule ever reads the current time — which is what makes evaluation
/// deterministic for a given snapshot.
/// </remarks>
/// <param name="CollectedAt">When collection ran, stamped at the boundary.</param>
/// <param name="Input">What the caller proposed.</param>
/// <param name="Backend">The server's own listener configuration.</param>
/// <param name="ProxyTrust">The configured proxy trust boundary.</param>
/// <param name="LocalAddresses">Classified local interface addresses.</param>
/// <param name="Listeners">What was seen on ports 80 and 443.</param>
/// <param name="Dns">The result of resolving the proposed hostname.</param>
public sealed record RemoteAccessDiagnosticSnapshot(
    DateTimeOffset CollectedAt,
    PublicationPolicyInput Input,
    BackendPostureObservation Backend,
    ProxyTrustObservation ProxyTrust,
    IReadOnlyList<ClassifiedAddress> LocalAddresses,
    IReadOnlyList<PortListenerObservation> Listeners,
    DnsObservation Dns);
