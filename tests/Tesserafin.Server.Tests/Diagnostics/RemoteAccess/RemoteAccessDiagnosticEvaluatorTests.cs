using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// The inference rules, and how far they refuse to go.
/// </summary>
/// <remarks>
/// Most of these assert a NEGATIVE: that a fact which looks like it implies reachability, or
/// CGNAT, or an available port, does not. Those are the assertions that decay first, because the
/// stronger conclusion is always the more useful-sounding one.
/// </remarks>
public sealed class RemoteAccessDiagnosticEvaluatorTests
{
    private static readonly DateTimeOffset _fixedInstant = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static ClassifiedAddress Local(string address)
    {
        var parsed = IPAddress.Parse(address);
        return new ClassifiedAddress(parsed, AddressClassifier.Classify(parsed));
    }

    private static RemoteAccessDiagnosticSnapshot Snapshot(
        BackendBindPosture posture = BackendBindPosture.LoopbackOnly,
        bool secureBootstrap = true,
        bool unixSocket = false,
        string[]? knownProxies = null,
        int? parsedProxies = null,
        bool? forwardedHeaders = null,
        IEnumerable<ClassifiedAddress>? localAddresses = null,
        ListenerObservationOutcome port80 = ListenerObservationOutcome.NoListenerObserved,
        ListenerObservationOutcome port443 = ListenerObservationOutcome.NoListenerObserved,
        DnsLookupOutcome dnsOutcome = DnsLookupOutcome.NotAttempted,
        string[]? dnsAddresses = null,
        string? hostname = null,
        bool? publishIPv4 = true,
        bool? publishIPv6 = false)
    {
        var proxies = knownProxies ?? Array.Empty<string>();
        var answers = (dnsAddresses ?? Array.Empty<string>()).Select(IPAddress.Parse).ToList();

        return new RemoteAccessDiagnosticSnapshot(
            _fixedInstant,
            new PublicationPolicyInput(hostname, publishIPv4, publishIPv6),
            new BackendPostureObservation(secureBootstrap, posture, unixSocket, 8096, 8920),
            new ProxyTrustObservation(proxies, parsedProxies ?? proxies.Length, forwardedHeaders ?? proxies.Length > 0),
            (localAddresses ?? Array.Empty<ClassifiedAddress>()).ToList(),
            new[]
            {
                new PortListenerObservation(80, port80),
                new PortListenerObservation(443, port443)
            },
            new DnsObservation(hostname, dnsOutcome, answers));
    }

    // ---------------------------------------------------------------- backend

    [Fact]
    public void WildcardBindIsHighSeverityEvenWithNothingElseWrong()
    {
        // The firewall is not observable from here, so a wildcard bind is the most exposure this
        // layer can see. Softening it on a guess about something unobservable is the failure mode.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(posture: BackendBindPosture.Wildcard));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.BackendWildcardBound);
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
        Assert.Equal(DiagnosticConfidence.Observed, finding.Confidence);
    }

    [Fact]
    public void SecureBootstrapWithLoopbackOnlyIsObservedButNotAVerdict()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(posture: BackendBindPosture.LoopbackOnly, secureBootstrap: true));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.SecureBootstrapActive));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.BackendLoopbackOnly));

        // Constraining the backend says nothing about the name, the proxy or the firewall.
        Assert.True(report.Has(RemoteAccessDiagnosticCode.ExternalReachabilityUnverified));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.CertificateReadinessUnverified));
        Assert.All(report.Findings, f => Assert.NotEqual(DiagnosticConfidence.None, f.Confidence));
    }

    [Fact]
    public void SecureBootstrapInactiveIsReported()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(secureBootstrap: false));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.SecureBootstrapInactive));
    }

    [Fact]
    public void ExplicitLanBindIsReported()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(posture: BackendBindPosture.ExplicitPrivateAddresses));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.BackendLanBound));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.BackendGloballyRoutableBound));
    }

    [Fact]
    public void ExplicitGloballyRoutableBindIsReportedSeparatelyAndAsHigh()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(posture: BackendBindPosture.ExplicitGloballyRoutableAddresses));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.BackendGloballyRoutableBound);
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
        Assert.False(report.Has(RemoteAccessDiagnosticCode.BackendLanBound));
    }

    [Fact]
    public void SecureBootstrapWithLoopbackOnlyProvesBackendConstraintAndNothingElse()
    {
        // Rule 10. The socket is constrained; the firewall, the name, the certificate and any
        // on-host proxy are all exactly as unknown as they were before.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(
            Snapshot(secureBootstrap: true, posture: BackendBindPosture.LoopbackOnly));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.BackendStructurallyConstrained);
        Assert.Equal(DiagnosticConfidence.Derived, finding.Confidence);
        Assert.False(report.Has(RemoteAccessDiagnosticCode.BackendPotentiallyPublic));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.ExternalReachabilityUnverified));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.FirewallStateUnknown));
    }

    [Theory]
    [InlineData(BackendBindPosture.Wildcard)]
    [InlineData(BackendBindPosture.ExplicitGloballyRoutableAddresses)]
    public void ASocketThatWouldAcceptOffHostIsReportedAsPotentiallyPublic(BackendBindPosture posture)
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(posture: posture));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.BackendPotentiallyPublic));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.BackendStructurallyConstrained));
    }

    [Fact]
    public void AnUnknownBindPostureIsNeitherConstrainedNorPubliclyExposed()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(posture: BackendBindPosture.Unknown));

        Assert.False(report.Has(RemoteAccessDiagnosticCode.BackendStructurallyConstrained));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.BackendPotentiallyPublic));
    }

    [Fact]
    public void UnknownBindPostureIsReportedAsUnknownNotAsSafe()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(posture: BackendBindPosture.Unknown));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.BackendExposureUnknown);
        Assert.Equal(DiagnosticConfidence.Unknown, finding.Confidence);
        Assert.False(report.Has(RemoteAccessDiagnosticCode.BackendLoopbackOnly));
    }

    [Fact]
    public void AUnixSocketBackendIsReported()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(unixSocket: true));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.BackendUnixSocketConfigured));
    }

    // --------------------------------------------------------------- listeners

    [Fact]
    public void AListenerOnPort80IsReportedAndOnlySuggestsAnIngressOwner()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(port80: ListenerObservationOutcome.ObservedListener));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.ListenerObservedOnPort80));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.PossibleExistingIngressOwner));
    }

    [Fact]
    public void AListenerOnPort443IsReported()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(port443: ListenerObservationOutcome.ObservedListener));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.ListenerObservedOnPort443));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.PossibleExistingIngressOwner));
    }

    [Fact]
    public void NoListenerObservedIsNeverAnAvailabilityClaim()
    {
        // The vocabulary gate. "Nothing was listening when I looked" and "you may bind here" are
        // different statements, and only the first is supported by a read-only listing.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot());

        Assert.True(report.Has(RemoteAccessDiagnosticCode.NoListenerObservedOnPort80));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.NoListenerObservedOnPort443));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.PossibleExistingIngressOwner));

        // Neither vocabulary offers a way to say a port is free — not the finding codes and not
        // the per-port outcomes, since a new outcome is the easier place to slip one in. No name
        // is exempted: the whole vocabulary has to survive the substring.
        var vocabulary = Enum.GetNames<RemoteAccessDiagnosticCode>()
            .Concat(Enum.GetNames<ListenerObservationOutcome>())
            .ToArray();

        Assert.DoesNotContain(vocabulary, n => n.Contains("Available", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vocabulary, n => n.Contains("Bindable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vocabulary, n => n.Contains("PortFree", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(ListenerObservationOutcome.InspectionDenied, RemoteAccessDiagnosticCode.ListenerInspectionDenied)]
    [InlineData(ListenerObservationOutcome.Unsupported, RemoteAccessDiagnosticCode.ListenerInspectionUnsupported)]
    [InlineData(ListenerObservationOutcome.Unknown, RemoteAccessDiagnosticCode.ListenerInspectionFailed)]
    public void AFailedInspectionIsReportedAsUnknownNotAsAbsence(ListenerObservationOutcome outcome, RemoteAccessDiagnosticCode expected)
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(port80: outcome, port443: outcome));

        var finding = report.Findings.Single(f => f.Code == expected);
        Assert.Equal(DiagnosticConfidence.Unknown, finding.Confidence);
        Assert.False(report.Has(RemoteAccessDiagnosticCode.NoListenerObservedOnPort80));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.NoListenerObservedOnPort443));
    }

    [Fact]
    public void TheThreeWaysOfNotSeeingAListenerStayApart()
    {
        // A denial an operator can grant, a platform that cannot answer, and an unexplained
        // failure call for three different actions. Collapsing them advises none of them.
        var denied = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(port80: ListenerObservationOutcome.InspectionDenied));
        var unsupported = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(port80: ListenerObservationOutcome.Unsupported));
        var failed = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(port80: ListenerObservationOutcome.Unknown));

        Assert.True(denied.Has(RemoteAccessDiagnosticCode.ListenerInspectionDenied));
        Assert.False(denied.Has(RemoteAccessDiagnosticCode.ListenerInspectionUnsupported));
        Assert.False(denied.Has(RemoteAccessDiagnosticCode.ListenerInspectionFailed));

        Assert.True(unsupported.Has(RemoteAccessDiagnosticCode.ListenerInspectionUnsupported));
        Assert.False(unsupported.Has(RemoteAccessDiagnosticCode.ListenerInspectionDenied));

        Assert.True(failed.Has(RemoteAccessDiagnosticCode.ListenerInspectionFailed));
        Assert.False(failed.Has(RemoteAccessDiagnosticCode.ListenerInspectionDenied));
        Assert.False(failed.Has(RemoteAccessDiagnosticCode.ListenerInspectionUnsupported));
    }

    // ------------------------------------------------------------- proxy trust

    [Fact]
    public void EmptyKnownProxiesIsReported()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot());
        Assert.True(report.Has(RemoteAccessDiagnosticCode.KnownProxiesAbsent));
    }

    [Fact]
    public void OneAcceptedKnownProxyIsReportedAsNormalized()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(knownProxies: new[] { "127.0.0.1" }));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.SingleKnownProxyNormalized));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.MultipleKnownProxiesConfigured));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.KnownProxiesMalformed));
    }

    [Fact]
    public void OneRejectedKnownProxyIsNotReportedAsNormalized()
    {
        // A single entry the parser threw away is not a narrow trust boundary; it is no trust
        // boundary, wearing the shape of one.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(
            Snapshot(knownProxies: new[] { "not-an-address" }, parsedProxies: 0));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.KnownProxiesMalformed));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.SingleKnownProxyNormalized));
    }

    [Fact]
    public void TwoKnownProxiesAreNotReportedAsASingleNormalizedOne()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(knownProxies: new[] { "127.0.0.1", "10.0.0.5" }));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.MultipleKnownProxiesConfigured));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.SingleKnownProxyNormalized));
    }

    [Fact]
    public void AMalformedKnownProxyEntryIsHighSeverity()
    {
        // An entry the server's own parser rejected grants none of the trust the operator believes
        // they configured, and nothing else in the system says so out loud.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(
            Snapshot(knownProxies: new[] { "not-an-address" }, parsedProxies: 0, forwardedHeaders: true));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.KnownProxiesMalformed);
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
    }

    [Fact]
    public void MultipleKnownProxiesAreReported()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(
            Snapshot(knownProxies: new[] { "127.0.0.1", "10.0.0.5" }, forwardedHeaders: true));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.MultipleKnownProxiesConfigured));
    }

    [Fact]
    public void ForwardedHeadersOffWithNoProxiesIsConsistent()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(forwardedHeaders: false));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.ForwardedHeadersDisabled));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.ForwardedHeaderTrustInconsistent));
    }

    [Fact]
    public void ForwardedHeadersOnWithAProxyIsConsistent()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(
            Snapshot(knownProxies: new[] { "127.0.0.1" }, forwardedHeaders: true));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.ForwardedHeadersEnabledConsistently));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.ForwardedHeaderTrustInconsistent));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void ADisagreementBetweenTrustAndForwardedHeadersIsContradictory(bool forwardedHeaders, int proxyCount)
    {
        var proxies = proxyCount == 0 ? Array.Empty<string>() : new[] { "127.0.0.1" };
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(knownProxies: proxies, forwardedHeaders: forwardedHeaders));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.ForwardedHeaderTrustInconsistent);
        Assert.Equal(DiagnosticConfidence.Contradictory, finding.Confidence);
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
    }

    [Fact]
    public void TheSameHostProxyLoopbackTrapIsDetected()
    {
        // RFC #241 §1.5, and the reason R1 precedes R2: an ingress on this host, a loopback
        // backend, and nothing trusted means every Internet visitor arrives as 127.0.0.1 and is
        // classified as LAN.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            posture: BackendBindPosture.LoopbackOnly,
            knownProxies: Array.Empty<string>(),
            port443: ListenerObservationOutcome.ObservedListener));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.SameHostProxyLoopbackTrustTrapPossible);
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
        Assert.Equal(DiagnosticConfidence.Derived, finding.Confidence);
    }

    [Fact]
    public void AnOrdinaryServerWithNoIngressIsNotCalledATrap()
    {
        // The other half of the gate. A detector that fires on every default installation would be
        // ignored within a week.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            posture: BackendBindPosture.LoopbackOnly,
            knownProxies: Array.Empty<string>()));

        Assert.False(report.Has(RemoteAccessDiagnosticCode.SameHostProxyLoopbackTrustTrapPossible));
    }

    [Fact]
    public void AConfiguredProxyRemovesTheTrapEvenWithAnIngressPresent()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            posture: BackendBindPosture.LoopbackOnly,
            knownProxies: new[] { "127.0.0.1" },
            forwardedHeaders: true,
            port443: ListenerObservationOutcome.ObservedListener));

        Assert.False(report.Has(RemoteAccessDiagnosticCode.SameHostProxyLoopbackTrustTrapPossible));
    }

    // ------------------------------------------------------------------- DNS

    [Fact]
    public void AnAbsentHostnameIsReportedAsAbsentNotInvalid()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(hostname: null, dnsOutcome: DnsLookupOutcome.NotAttempted));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.HostnameNotProvided));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.HostnameSyntacticallyInvalid));
    }

    [Fact]
    public void ARejectedHostnameIsReportedAsInvalid()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(
            Snapshot(hostname: "https://media.example.org", dnsOutcome: DnsLookupOutcome.NotAttempted));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.HostnameSyntacticallyInvalid));
    }

    [Theory]
    [InlineData(DnsLookupOutcome.NoAddressRecords, RemoteAccessDiagnosticCode.DnsNoAddressRecords)]
    [InlineData(DnsLookupOutcome.TimedOut, RemoteAccessDiagnosticCode.DnsLookupTimedOut)]
    [InlineData(DnsLookupOutcome.Cancelled, RemoteAccessDiagnosticCode.DnsLookupCancelled)]
    [InlineData(DnsLookupOutcome.ResolverFailure, RemoteAccessDiagnosticCode.DnsLookupFailed)]
    public void EachWayALookupCanFailKeepsItsOwnCode(DnsLookupOutcome outcome, RemoteAccessDiagnosticCode expected)
    {
        // Collapsing these would send an operator to edit a zone file when their resolver was slow.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(
            Snapshot(hostname: "media.example.org", dnsOutcome: outcome));

        Assert.True(report.Has(expected));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.DnsLookupSucceeded));
    }

    [Fact]
    public void AnIPv4OnlyAnswerIsReportedAsSuch()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7" }));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.DnsResultContainsIPv4));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.DnsResultContainsIPv6));
    }

    [Fact]
    public void AnIPv6OnlyAnswerIsReportedAsSuch()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "2001:db8::1" },
            publishIPv4: false,
            publishIPv6: true));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.DnsResultContainsIPv6));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.DnsResultContainsIPv4));
    }

    [Fact]
    public void ADualStackAnswerIsReportedAsBoth()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7", "2001:db8::1" },
            publishIPv4: true,
            publishIPv6: true));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.DnsResultContainsIPv4));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.DnsResultContainsIPv6));
    }

    // -------------------------------------------------------------- topology

    [Fact]
    public void AMatchingGlobalIPv4AnswerPermitsOnlyDirectAddressPossible()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            localAddresses: new[] { Local("203.0.113.7") },
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7" }));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.DnsAddressMatchesLocalGlobalAddress));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.DirectPublicAddressPossible));

        // The packet still has to cross a firewall this layer cannot see.
        Assert.True(report.Has(RemoteAccessDiagnosticCode.ExternalReachabilityUnverified));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.FirewallStateUnknown));
    }

    [Fact]
    public void AMatchingGlobalIPv6AnswerPermitsOnlyDirectAddressPossible()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            localAddresses: new[] { Local("2001:db8::1") },
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "2001:db8::1" },
            publishIPv4: false,
            publishIPv6: true));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.DirectPublicAddressPossible));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.ExternalReachabilityUnverified));
    }

    [Fact]
    public void PrivateAddressingWithDnsElsewherePermitsNatButNeverCgNat()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            localAddresses: new[] { Local("192.168.1.5") },
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7" }));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.NatOrUpstreamProxyPossiblyRequired));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.DnsAddressMatchesNoLocalAddress));

        // Ordinary NAT and carrier-grade NAT are different problems with different remedies.
        Assert.False(report.Has(RemoteAccessDiagnosticCode.CgNatSignalObserved));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.CgNatNotDeterminable));
    }

    [Fact]
    public void SharedAddressSpaceIsTheOnlyThingThatRaisesACgNatSignal()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            localAddresses: new[] { Local("100.64.12.9") },
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7" }));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.SharedAddressSpaceObserved));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.CgNatSignalObserved));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.CgNatNotDeterminable));

        // Even this does not settle the upstream topology; it is a signal, not a conclusion.
        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.CgNatSignalObserved);
        Assert.Equal(DiagnosticConfidence.Derived, finding.Confidence);
    }

    [Fact]
    public void HavingNoGlobalAddressAtAllIsNotReportedAsCgNat()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            localAddresses: new[] { Local("127.0.0.1"), Local("192.168.1.5") }));

        Assert.False(report.Has(RemoteAccessDiagnosticCode.CgNatSignalObserved));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.CgNatNotDeterminable));
    }

    [Fact]
    public void ADnsMatchIsNeverReportedAsExternalReachability()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            localAddresses: new[] { Local("203.0.113.7") },
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7" }));

        var reachability = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.ExternalReachabilityUnverified);
        Assert.Equal(DiagnosticConfidence.Unverified, reachability.Confidence);
    }

    // -------------------------------------------------------- address family

    [Fact]
    public void ACorrectIPv4ResultDoesNotSuppressAnUnsatisfiedIPv6Policy()
    {
        // The operator who publishes IPv4, forgets the IPv6 firewall and is told everything agrees
        // is exactly who this rule protects.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7" },
            publishIPv4: true,
            publishIPv6: true));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.IpFamilyPolicyContradicted);
        Assert.Equal(DiagnosticConfidence.Contradictory, finding.Confidence);
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
    }

    [Fact]
    public void AFamilyThatPolicyExcludesButThatDnsPublishesDisagrees()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7", "2001:db8::1" },
            publishIPv4: true,
            publishIPv6: false));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.IpFamilyPolicyContradicted));
    }

    [Fact]
    public void APolicyThatMatchesTheAnswersDoesNotDisagree()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7", "2001:db8::1" },
            publishIPv4: true,
            publishIPv6: true));

        Assert.False(report.Has(RemoteAccessDiagnosticCode.IpFamilyPolicyContradicted));
        Assert.False(report.Has(RemoteAccessDiagnosticCode.IpFamilyPolicyUnresolved));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, null)]
    [InlineData(null, null)]
    public void AnUnstatedFamilyPolicyIsReportedRatherThanReadAsNo(bool? publishIPv4, bool? publishIPv6)
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            publishIPv4: publishIPv4,
            publishIPv6: publishIPv6));

        var finding = report.Findings.Single(f => f.Code == RemoteAccessDiagnosticCode.IpFamilyPolicyUnresolved);
        Assert.Equal(DiagnosticConfidence.Unknown, finding.Confidence);
    }

    [Fact]
    public void AnUnstatedFamilyPolicySurvivesASuccessfulLookupForTheOtherFamily()
    {
        // Rule 12 again, from the other direction: a clean IPv4 answer must not close the IPv6
        // question that nobody ever opened.
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7" },
            publishIPv4: true,
            publishIPv6: null));

        Assert.True(report.Has(RemoteAccessDiagnosticCode.DnsResultContainsIPv4));
        Assert.True(report.Has(RemoteAccessDiagnosticCode.IpFamilyPolicyUnresolved));
    }

    // ----------------------------------------------------- permanent exclusions

    [Fact]
    public void EveryReportLeavesTheSameFourThingsUnverified()
    {
        var reports = new[]
        {
            RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot()),
            RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(posture: BackendBindPosture.Wildcard)),
            RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot(
                localAddresses: new[] { Local("203.0.113.7") },
                hostname: "media.example.org",
                dnsOutcome: DnsLookupOutcome.Answered,
                dnsAddresses: new[] { "203.0.113.7" }))
        };

        foreach (var report in reports)
        {
            Assert.True(report.Has(RemoteAccessDiagnosticCode.ExternalReachabilityUnverified));
            Assert.True(report.Has(RemoteAccessDiagnosticCode.CertificateReadinessUnverified));
            Assert.True(report.Has(RemoteAccessDiagnosticCode.FirewallStateUnknown));
            Assert.True(report.Has(RemoteAccessDiagnosticCode.RouterMappingUnknown));
        }
    }

    [Fact]
    public void EvaluationIsDeterministic()
    {
        var snapshot = Snapshot(
            localAddresses: new[] { Local("192.168.1.5"), Local("100.64.0.1") },
            hostname: "media.example.org",
            dnsOutcome: DnsLookupOutcome.Answered,
            dnsAddresses: new[] { "203.0.113.7" },
            port80: ListenerObservationOutcome.ObservedListener);

        var first = RemoteAccessDiagnosticEvaluator.Evaluate(snapshot);
        var second = RemoteAccessDiagnosticEvaluator.Evaluate(snapshot);
        var third = RemoteAccessDiagnosticEvaluator.Evaluate(snapshot);

        Assert.Equal(first.Findings, second.Findings);
        Assert.Equal(second.Findings, third.Findings);
    }

    [Fact]
    public void NoFindingEverCarriesADefaultEnumValue()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot());

        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, f =>
        {
            Assert.NotEqual(RemoteAccessDiagnosticCode.None, f.Code);
            Assert.NotEqual(DiagnosticConfidence.None, f.Confidence);
            Assert.NotEqual(DiagnosticSeverity.None, f.Severity);
        });
    }

    [Fact]
    public void NullSnapshotThrows()
    {
        Assert.Throws<ArgumentNullException>(() => RemoteAccessDiagnosticEvaluator.Evaluate(null!));
    }
}
