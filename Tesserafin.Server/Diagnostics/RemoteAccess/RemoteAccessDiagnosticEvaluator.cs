using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Turns observations into findings, conservatively.
/// </summary>
/// <remarks>
/// <para>
/// A pure function of its snapshot: no configuration, no clock, no I/O, no state. Everything it
/// could possibly say is already in the argument, which is what makes the inference rules
/// testable without a network and what makes the same snapshot always produce the same report.
/// </para>
/// <para>
/// The rules are deliberately weaker than they could be. Every one of them stops at what the
/// evidence actually supports: a matching DNS answer permits "possible", never "reachable";
/// private addressing permits "NAT is probably involved", never "carrier-grade NAT"; an unused
/// port permits "nothing was listening", never "the port is free". The failure mode this guards
/// against is not a wrong answer, it is a confident one.
/// </para>
/// </remarks>
public static class RemoteAccessDiagnosticEvaluator
{
    /// <summary>
    /// Evaluates a snapshot.
    /// </summary>
    /// <param name="snapshot">The observations to judge.</param>
    /// <returns>The report. Deterministic for a given snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
    public static RemoteAccessDiagnosticReport Evaluate(RemoteAccessDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var findings = new List<RemoteAccessFinding>();

        AddBackendFindings(snapshot, findings);
        AddListenerFindings(snapshot, findings);
        AddProxyTrustFindings(snapshot, findings);
        AddLocalAddressFindings(snapshot, findings);
        AddDnsFindings(snapshot, findings);
        AddTopologyFindings(snapshot, findings);
        AddAddressFamilyFindings(snapshot, findings);
        AddPermanentExclusions(findings);

        return new RemoteAccessDiagnosticReport(snapshot, findings);
    }

    private static void Add(
        List<RemoteAccessFinding> findings,
        RemoteAccessDiagnosticCode code,
        DiagnosticConfidence confidence,
        DiagnosticSeverity severity)
        => findings.Add(new RemoteAccessFinding(code, confidence, severity));

    private static void AddBackendFindings(RemoteAccessDiagnosticSnapshot snapshot, List<RemoteAccessFinding> findings)
    {
        var backend = snapshot.Backend;

        Add(
            findings,
            backend.SecureBootstrapActive
                ? RemoteAccessDiagnosticCode.SecureBootstrapActive
                : RemoteAccessDiagnosticCode.SecureBootstrapInactive,
            DiagnosticConfidence.Observed,
            DiagnosticSeverity.Informational);

        switch (backend.Posture)
        {
            case BackendBindPosture.LoopbackOnly:
                // An observed constraint on the backend. Emphatically not proof that publishing
                // this server would be safe: nothing about the proxy, the firewall or the name has
                // been established by this fact.
                Add(findings, RemoteAccessDiagnosticCode.BackendLoopbackOnly, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
                break;

            case BackendBindPosture.Wildcard:
                // High regardless of what the firewall might be doing. The firewall is not
                // observable from here, so a wildcard bind is the most exposure this layer can
                // actually see, and it must not be softened by a guess about something it cannot.
                Add(findings, RemoteAccessDiagnosticCode.BackendWildcardBound, DiagnosticConfidence.Observed, DiagnosticSeverity.High);
                break;

            case BackendBindPosture.ExplicitAddresses:
                Add(findings, RemoteAccessDiagnosticCode.BackendLanBound, DiagnosticConfidence.Observed, DiagnosticSeverity.Advisory);
                break;

            default:
                Add(findings, RemoteAccessDiagnosticCode.BackendExposureUnknown, DiagnosticConfidence.Unknown, DiagnosticSeverity.Advisory);
                break;
        }

        if (backend.UnixSocketConfigured)
        {
            Add(findings, RemoteAccessDiagnosticCode.BackendUnixSocketConfigured, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
        }
    }

    private static void AddListenerFindings(RemoteAccessDiagnosticSnapshot snapshot, List<RemoteAccessFinding> findings)
    {
        var anyObserved = false;
        var anyUnavailable = false;

        foreach (var observation in snapshot.Listeners)
        {
            switch (observation.Outcome)
            {
                case ListenerObservationOutcome.ObservedListener:
                    anyObserved = true;
                    Add(
                        findings,
                        observation.Port == 443
                            ? RemoteAccessDiagnosticCode.ListenerObservedOnPort443
                            : RemoteAccessDiagnosticCode.ListenerObservedOnPort80,
                        DiagnosticConfidence.Observed,
                        DiagnosticSeverity.Informational);
                    break;

                case ListenerObservationOutcome.NoListenerObserved:
                    // Says only that nothing was listening at the instant of the read. Whether a
                    // future service could bind here is a question about privilege and policy that
                    // a read-only listing cannot answer.
                    Add(
                        findings,
                        observation.Port == 443
                            ? RemoteAccessDiagnosticCode.NoListenerObservedOnPort443
                            : RemoteAccessDiagnosticCode.NoListenerObservedOnPort80,
                        DiagnosticConfidence.Observed,
                        DiagnosticSeverity.Informational);
                    break;

                default:
                    anyUnavailable = true;
                    break;
            }
        }

        if (anyUnavailable)
        {
            Add(findings, RemoteAccessDiagnosticCode.ListenerInspectionUnavailable, DiagnosticConfidence.Unknown, DiagnosticSeverity.Advisory);
        }

        if (anyObserved)
        {
            // Something owns the port. Which product it is cannot be known without interrogating
            // the owning process, which this layer does not do.
            Add(findings, RemoteAccessDiagnosticCode.PossibleExistingIngressOwner, DiagnosticConfidence.Derived, DiagnosticSeverity.Advisory);
        }
    }

    private static void AddProxyTrustFindings(RemoteAccessDiagnosticSnapshot snapshot, List<RemoteAccessFinding> findings)
    {
        var trust = snapshot.ProxyTrust;
        var configuredCount = trust.ConfiguredKnownProxies.Count;

        if (configuredCount == 0)
        {
            Add(findings, RemoteAccessDiagnosticCode.KnownProxiesAbsent, DiagnosticConfidence.Observed, DiagnosticSeverity.Advisory);
        }
        else
        {
            if (trust.ParsedKnownProxyCount < configuredCount)
            {
                // An entry the server's own parser rejected is an entry that silently does not
                // grant the trust the operator believes they configured.
                Add(findings, RemoteAccessDiagnosticCode.KnownProxiesMalformed, DiagnosticConfidence.Observed, DiagnosticSeverity.High);
            }

            if (configuredCount > 1)
            {
                Add(findings, RemoteAccessDiagnosticCode.MultipleKnownProxiesConfigured, DiagnosticConfidence.Observed, DiagnosticSeverity.Advisory);
            }
        }

        if (!trust.ForwardedHeadersEnabled)
        {
            Add(findings, RemoteAccessDiagnosticCode.ForwardedHeadersDisabled, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
        }

        // The server enables forwarded-header processing exactly when at least one known proxy is
        // configured. Any other combination means the two halves of the trust boundary disagree.
        var expectedEnabled = configuredCount > 0;
        if (trust.ForwardedHeadersEnabled != expectedEnabled)
        {
            Add(findings, RemoteAccessDiagnosticCode.ForwardedHeaderTrustInconsistent, DiagnosticConfidence.Contradictory, DiagnosticSeverity.High);
        }
        else if (trust.ForwardedHeadersEnabled)
        {
            Add(findings, RemoteAccessDiagnosticCode.ForwardedHeadersEnabledConsistently, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
        }

        // The flagship trap of RFC #241 §1.5, and the reason R1 precedes R2. A proxy on this host
        // forwarding to a loopback backend, with nothing trusted, makes every Internet visitor
        // arrive as 127.0.0.1 — which the server classifies as LAN, short-circuiting the remote
        // access policy entirely.
        var ingressPresent = false;
        foreach (var observation in snapshot.Listeners)
        {
            if (observation.Outcome == ListenerObservationOutcome.ObservedListener)
            {
                ingressPresent = true;
                break;
            }
        }

        var backendReachableFromSameHost =
            snapshot.Backend.Posture is BackendBindPosture.LoopbackOnly or BackendBindPosture.Wildcard;

        if (configuredCount == 0 && ingressPresent && backendReachableFromSameHost)
        {
            Add(findings, RemoteAccessDiagnosticCode.SameHostProxyLoopbackTrustTrapPossible, DiagnosticConfidence.Derived, DiagnosticSeverity.High);
        }
    }

    private static void AddLocalAddressFindings(RemoteAccessDiagnosticSnapshot snapshot, List<RemoteAccessFinding> findings)
    {
        var sawPrivate = false;
        var sawShared = false;

        foreach (var address in snapshot.LocalAddresses)
        {
            sawPrivate |= address.Class == AddressClass.Private;
            sawShared |= address.Class == AddressClass.SharedAddressSpace;
        }

        if (sawPrivate)
        {
            Add(findings, RemoteAccessDiagnosticCode.PrivateAddressingObserved, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
        }

        if (sawShared)
        {
            Add(findings, RemoteAccessDiagnosticCode.SharedAddressSpaceObserved, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
        }
    }

    private static void AddDnsFindings(RemoteAccessDiagnosticSnapshot snapshot, List<RemoteAccessFinding> findings)
    {
        var dns = snapshot.Dns;

        if (dns.Outcome == DnsLookupOutcome.NotAttempted)
        {
            Add(
                findings,
                string.IsNullOrWhiteSpace(snapshot.Input.ProposedHostname)
                    ? RemoteAccessDiagnosticCode.HostnameNotProvided
                    : RemoteAccessDiagnosticCode.HostnameSyntacticallyInvalid,
                DiagnosticConfidence.Observed,
                DiagnosticSeverity.Informational);
            return;
        }

        switch (dns.Outcome)
        {
            case DnsLookupOutcome.Answered:
                Add(findings, RemoteAccessDiagnosticCode.DnsLookupSucceeded, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
                break;
            case DnsLookupOutcome.NoAddressRecords:
                Add(findings, RemoteAccessDiagnosticCode.DnsNoAddressRecords, DiagnosticConfidence.Observed, DiagnosticSeverity.Advisory);
                break;
            case DnsLookupOutcome.TimedOut:
                Add(findings, RemoteAccessDiagnosticCode.DnsLookupTimedOut, DiagnosticConfidence.Unknown, DiagnosticSeverity.Advisory);
                break;
            case DnsLookupOutcome.Cancelled:
                Add(findings, RemoteAccessDiagnosticCode.DnsLookupCancelled, DiagnosticConfidence.Unknown, DiagnosticSeverity.Informational);
                break;
            default:
                Add(findings, RemoteAccessDiagnosticCode.DnsLookupFailed, DiagnosticConfidence.Unknown, DiagnosticSeverity.Advisory);
                break;
        }

        if (dns.Outcome != DnsLookupOutcome.Answered)
        {
            return;
        }

        var hasIPv4 = false;
        var hasIPv6 = false;
        foreach (var address in dns.Addresses)
        {
            hasIPv4 |= address.AddressFamily == AddressFamily.InterNetwork;
            hasIPv6 |= address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        if (hasIPv4)
        {
            Add(findings, RemoteAccessDiagnosticCode.DnsResultContainsIPv4, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
        }

        if (hasIPv6)
        {
            Add(findings, RemoteAccessDiagnosticCode.DnsResultContainsIPv6, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
        }
    }

    private static void AddTopologyFindings(RemoteAccessDiagnosticSnapshot snapshot, List<RemoteAccessFinding> findings)
    {
        var sawShared = false;
        var sawPrivate = false;
        var globalLocalAddresses = new HashSet<string>(StringComparer.Ordinal);

        foreach (var address in snapshot.LocalAddresses)
        {
            sawShared |= address.Class == AddressClass.SharedAddressSpace;
            sawPrivate |= address.Class == AddressClass.Private;

            if (address.Class == AddressClass.GloballyRoutable)
            {
                globalLocalAddresses.Add(address.Address.ToString());
            }
        }

        // Shared address space is the ONLY locally observable CGNAT signal, and even it does not
        // establish the upstream topology. Everything else — including having no global address at
        // all — leaves the question open, and saying so is the honest answer.
        if (sawShared)
        {
            Add(findings, RemoteAccessDiagnosticCode.CgNatSignalObserved, DiagnosticConfidence.Derived, DiagnosticSeverity.Advisory);
        }
        else
        {
            Add(findings, RemoteAccessDiagnosticCode.CgNatNotDeterminable, DiagnosticConfidence.Unknown, DiagnosticSeverity.Informational);
        }

        if (snapshot.Dns.Outcome != DnsLookupOutcome.Answered || snapshot.Dns.Addresses.Count == 0)
        {
            return;
        }

        var matchesLocalGlobal = false;
        var matchesAnyLocal = false;
        var localAddresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in snapshot.LocalAddresses)
        {
            localAddresses.Add(address.Address.ToString());
        }

        foreach (var answer in snapshot.Dns.Addresses)
        {
            var normalized = AddressClassifier.Normalize(answer).ToString();
            matchesAnyLocal |= localAddresses.Contains(normalized);
            matchesLocalGlobal |= globalLocalAddresses.Contains(normalized);
        }

        if (matchesLocalGlobal)
        {
            // "Possible", never "reachable". The packet still has to cross a firewall this layer
            // cannot see, from a vantage point it does not have.
            Add(findings, RemoteAccessDiagnosticCode.DnsAddressMatchesLocalGlobalAddress, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);
            Add(findings, RemoteAccessDiagnosticCode.DirectPublicAddressPossible, DiagnosticConfidence.Derived, DiagnosticSeverity.Informational);
        }

        if (!matchesAnyLocal)
        {
            Add(findings, RemoteAccessDiagnosticCode.DnsAddressMatchesNoLocalAddress, DiagnosticConfidence.Observed, DiagnosticSeverity.Informational);

            if (sawPrivate || sawShared)
            {
                // Something translates or proxies between the name and this host. Which of the two,
                // and how many layers, is not observable here.
                Add(findings, RemoteAccessDiagnosticCode.NatOrUpstreamProxyPossiblyRequired, DiagnosticConfidence.Derived, DiagnosticSeverity.Advisory);
            }
        }
    }

    private static void AddAddressFamilyFindings(RemoteAccessDiagnosticSnapshot snapshot, List<RemoteAccessFinding> findings)
    {
        if (snapshot.Dns.Outcome != DnsLookupOutcome.Answered)
        {
            return;
        }

        var hasIPv4 = false;
        var hasIPv6 = false;
        foreach (var address in snapshot.Dns.Addresses)
        {
            hasIPv4 |= address.AddressFamily == AddressFamily.InterNetwork;
            hasIPv6 |= address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        // The families are judged independently and the results are OR-ed, never AND-ed. A correct
        // IPv4 result must not conceal an IPv6 one: the operator who publishes IPv4, forgets the
        // IPv6 firewall and is told everything agrees is the exact person this rule protects.
        var ipv4Disagrees = snapshot.Input.PublishIPv4 != hasIPv4;
        var ipv6Disagrees = snapshot.Input.PublishIPv6 != hasIPv6;

        if (ipv4Disagrees || ipv6Disagrees)
        {
            Add(findings, RemoteAccessDiagnosticCode.IpFamilyPolicyDisagreement, DiagnosticConfidence.Contradictory, DiagnosticSeverity.High);
        }
    }

    private static void AddPermanentExclusions(List<RemoteAccessFinding> findings)
    {
        // Emitted unconditionally, in every report, whatever else was observed. They are the
        // standing reminder that a green-looking set of local findings is not a public-access
        // verdict, and there is no evidence this layer could gather that would remove them.
        Add(findings, RemoteAccessDiagnosticCode.ExternalReachabilityUnverified, DiagnosticConfidence.Unverified, DiagnosticSeverity.Informational);
        Add(findings, RemoteAccessDiagnosticCode.CertificateReadinessUnverified, DiagnosticConfidence.Unverified, DiagnosticSeverity.Informational);
        Add(findings, RemoteAccessDiagnosticCode.FirewallStateUnknown, DiagnosticConfidence.Unverified, DiagnosticSeverity.Informational);
        Add(findings, RemoteAccessDiagnosticCode.RouterMappingUnknown, DiagnosticConfidence.Unverified, DiagnosticSeverity.Informational);
    }
}
