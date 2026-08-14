using System;
using System.Collections.Generic;
using System.Net.Sockets;
using Tesserafin.Server.Api.RemoteAccess.Models;
using Tesserafin.Server.Diagnostics.RemoteAccess;

namespace Tesserafin.Server.Api.RemoteAccess;

/// <summary>
/// The one place the internal diagnostic types become the published contract.
/// </summary>
/// <remarks>
/// WHY A PROJECTION AND NOT SERIALISATION. Serialising <c>RemoteAccessDiagnosticReport</c> and its
/// snapshot directly would make every internal record the contract: renaming a field, adding a
/// constructor parameter or tightening a nullability would silently change what every generated
/// client sees, with no diff in <c>openapi/openapi.json</c> to review. Projecting costs one
/// explicit mapping and buys a boundary that a reviewer can see.
///
/// EVERY MAPPING IS EXHAUSTIVE AND THROWS ON A VALUE IT DOES NOT KNOW. A <c>default</c> arm that
/// returned <c>None</c> would turn "someone added a diagnostic code and nobody decided how to
/// publish it" into a report that quietly says nothing — the worst possible outcome for a
/// diagnostic. The switches below have no such arm, and a lockstep test walks every internal enum
/// value through them so the failure lands in CI rather than in production.
/// </remarks>
public static class RemoteAccessDiagnosticsProjector
{
    /// <summary>
    /// Maps the wire request onto the engine's input record.
    /// </summary>
    /// <param name="request">The bound request.</param>
    /// <returns>The engine input.</returns>
    public static PublicationPolicyInput ToInput(RemoteAccessDiagnosticsRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The three-state wire value exists precisely so that "unspecified" survives the trip. A
        // bool would have collapsed it into "do not publish" and answered a question nobody asked.
        return new PublicationPolicyInput(
            request.Hostname,
            ToNullableBool(request.IPv4Policy),
            ToNullableBool(request.IPv6Policy));
    }

    /// <summary>
    /// Maps the engine's report onto the wire projection.
    /// </summary>
    /// <param name="report">The engine report.</param>
    /// <returns>The wire report.</returns>
    public static RemoteAccessDiagnosticsReportDto ToWire(RemoteAccessDiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var snapshot = report.Snapshot;

        var findings = new List<RemoteAccessFindingDto>(report.Findings.Count);
        // Index order, never sorted or grouped: the engine's sequence is part of the contract.
        for (var i = 0; i < report.Findings.Count; i++)
        {
            var finding = report.Findings[i];
            findings.Add(new RemoteAccessFindingDto
            {
                Code = ToWireCode(finding.Code),
                Confidence = ToWireConfidence(finding.Confidence),
                Severity = ToWireSeverity(finding.Severity)
            });
        }

        var listeners = new List<RemoteAccessListenerObservationDto>(snapshot.Listeners.Count);
        for (var i = 0; i < snapshot.Listeners.Count; i++)
        {
            listeners.Add(new RemoteAccessListenerObservationDto
            {
                Port = snapshot.Listeners[i].Port,
                Outcome = ToWireListenerOutcome(snapshot.Listeners[i].Outcome)
            });
        }

        return new RemoteAccessDiagnosticsReportDto
        {
            SchemaVersion = report.SchemaVersion,
            CollectedAt = snapshot.CollectedAt,
            Input = new RemoteAccessPolicyInputDto
            {
                // The NORMALISED hostname, not the raw one: this field answers "what did you
                // actually look up", and echoing the raw string back would answer a different
                // question. Null when nothing usable was supplied.
                Hostname = snapshot.Dns.NormalizedHostname,
                IPv4Policy = ToWirePolicy(snapshot.Input.PublishIPv4),
                IPv6Policy = ToWirePolicy(snapshot.Input.PublishIPv6)
            },
            Backend = new RemoteAccessBackendObservationDto
            {
                SecureBootstrapActive = snapshot.Backend.SecureBootstrapActive,
                Posture = ToWirePosture(snapshot.Backend.Posture),
                UnixSocketConfigured = snapshot.Backend.UnixSocketConfigured,
                InternalHttpPort = snapshot.Backend.InternalHttpPort,
                InternalHttpsPort = snapshot.Backend.InternalHttpsPort
            },
            ProxyTrust = new RemoteAccessProxyTrustObservationDto
            {
                ConfiguredKnownProxyCount = snapshot.ProxyTrust.ConfiguredKnownProxies.Count,
                ParsedKnownProxyCount = snapshot.ProxyTrust.ParsedKnownProxyCount,
                ForwardedHeadersEnabled = snapshot.ProxyTrust.ForwardedHeadersEnabled
            },
            LocalAddresses = Census(snapshot.LocalAddresses),
            Listeners = listeners,
            Dns = new RemoteAccessDnsObservationDto
            {
                NormalizedHostname = snapshot.Dns.NormalizedHostname,
                Outcome = ToWireDnsOutcome(snapshot.Dns.Outcome),
                AddressCount = snapshot.Dns.Addresses.Count,
                ContainsIPv4 = ContainsFamily(snapshot.Dns.Addresses, AddressFamily.InterNetwork),
                ContainsIPv6 = ContainsFamily(snapshot.Dns.Addresses, AddressFamily.InterNetworkV6)
            },
            Findings = findings
        };
    }

    private static bool ContainsFamily(IReadOnlyList<System.Net.IPAddress> addresses, AddressFamily family)
    {
        for (var i = 0; i < addresses.Count; i++)
        {
            if (addresses[i].AddressFamily == family)
            {
                return true;
            }
        }

        return false;
    }

    private static RemoteAccessLocalAddressCensusDto Census(IReadOnlyList<ClassifiedAddress> addresses)
    {
        var census = new RemoteAccessLocalAddressCensusDto();
        for (var i = 0; i < addresses.Count; i++)
        {
            switch (addresses[i].Class)
            {
                case AddressClass.Loopback: census.LoopbackCount++; break;
                case AddressClass.LinkLocal: census.LinkLocalCount++; break;
                case AddressClass.Private: census.PrivateCount++; break;
                case AddressClass.SharedAddressSpace: census.SharedAddressSpaceCount++; break;
                case AddressClass.GloballyRoutable: census.GloballyRoutableCount++; break;
                case AddressClass.Multicast: census.MulticastCount++; break;
                case AddressClass.Unspecified: census.UnspecifiedCount++; break;
                case AddressClass.Other: census.OtherCount++; break;
                case AddressClass.None:
                default:
                    throw new InvalidOperationException(
                        $"Address class '{addresses[i].Class}' has no intentional wire disposition.");
            }
        }

        return census;
    }

    private static bool? ToNullableBool(RemoteAccessPublicationPolicy? policy) => policy switch
    {
        RemoteAccessPublicationPolicy.Unspecified => null,
        RemoteAccessPublicationPolicy.DoNotPublish => false,
        RemoteAccessPublicationPolicy.Publish => true,
        // Unreachable through model binding, which requires the field. Explicit rather than a
        // silent `null`, because "absent" and "unspecified" must never merge by accident.
        _ => throw new InvalidOperationException($"Publication policy '{policy}' is not a known wire value.")
    };

    private static RemoteAccessPublicationPolicy ToWirePolicy(bool? value) => value switch
    {
        null => RemoteAccessPublicationPolicy.Unspecified,
        false => RemoteAccessPublicationPolicy.DoNotPublish,
        true => RemoteAccessPublicationPolicy.Publish
    };

    private static RemoteAccessFindingConfidence ToWireConfidence(DiagnosticConfidence value) => value switch
    {
        DiagnosticConfidence.None => RemoteAccessFindingConfidence.None,
        DiagnosticConfidence.Observed => RemoteAccessFindingConfidence.Observed,
        DiagnosticConfidence.Derived => RemoteAccessFindingConfidence.Derived,
        DiagnosticConfidence.Unverified => RemoteAccessFindingConfidence.Unverified,
        DiagnosticConfidence.Unknown => RemoteAccessFindingConfidence.Unknown,
        DiagnosticConfidence.Contradictory => RemoteAccessFindingConfidence.Contradictory,
        _ => throw new InvalidOperationException($"Confidence '{value}' has no intentional wire disposition.")
    };

    private static RemoteAccessFindingSeverity ToWireSeverity(DiagnosticSeverity value) => value switch
    {
        DiagnosticSeverity.None => RemoteAccessFindingSeverity.None,
        DiagnosticSeverity.Informational => RemoteAccessFindingSeverity.Informational,
        DiagnosticSeverity.Advisory => RemoteAccessFindingSeverity.Advisory,
        DiagnosticSeverity.High => RemoteAccessFindingSeverity.High,
        _ => throw new InvalidOperationException($"Severity '{value}' has no intentional wire disposition.")
    };

    private static RemoteAccessBackendBindPosture ToWirePosture(BackendBindPosture value) => value switch
    {
        BackendBindPosture.None => RemoteAccessBackendBindPosture.None,
        BackendBindPosture.LoopbackOnly => RemoteAccessBackendBindPosture.LoopbackOnly,
        BackendBindPosture.Wildcard => RemoteAccessBackendBindPosture.Wildcard,
        BackendBindPosture.ExplicitPrivateAddresses => RemoteAccessBackendBindPosture.ExplicitPrivateAddresses,
        BackendBindPosture.Unknown => RemoteAccessBackendBindPosture.Unknown,
        BackendBindPosture.ExplicitGloballyRoutableAddresses => RemoteAccessBackendBindPosture.ExplicitGloballyRoutableAddresses,
        _ => throw new InvalidOperationException($"Bind posture '{value}' has no intentional wire disposition.")
    };

    private static RemoteAccessListenerOutcome ToWireListenerOutcome(ListenerObservationOutcome value) => value switch
    {
        ListenerObservationOutcome.None => RemoteAccessListenerOutcome.None,
        ListenerObservationOutcome.ObservedListener => RemoteAccessListenerOutcome.ObservedListener,
        ListenerObservationOutcome.NoListenerObserved => RemoteAccessListenerOutcome.NoListenerObserved,
        ListenerObservationOutcome.InspectionDenied => RemoteAccessListenerOutcome.InspectionDenied,
        ListenerObservationOutcome.Unsupported => RemoteAccessListenerOutcome.Unsupported,
        ListenerObservationOutcome.Unknown => RemoteAccessListenerOutcome.Unknown,
        _ => throw new InvalidOperationException($"Listener outcome '{value}' has no intentional wire disposition.")
    };

    private static RemoteAccessDnsOutcome ToWireDnsOutcome(DnsLookupOutcome value) => value switch
    {
        DnsLookupOutcome.None => RemoteAccessDnsOutcome.None,
        DnsLookupOutcome.NotAttempted => RemoteAccessDnsOutcome.NotAttempted,
        DnsLookupOutcome.Answered => RemoteAccessDnsOutcome.Answered,
        DnsLookupOutcome.NoAddressRecords => RemoteAccessDnsOutcome.NoAddressRecords,
        DnsLookupOutcome.TimedOut => RemoteAccessDnsOutcome.TimedOut,
        DnsLookupOutcome.Cancelled => RemoteAccessDnsOutcome.Cancelled,
        DnsLookupOutcome.ResolverFailure => RemoteAccessDnsOutcome.ResolverFailure,
        _ => throw new InvalidOperationException($"DNS outcome '{value}' has no intentional wire disposition.")
    };

    /// <summary>
    /// Maps an internal diagnostic code to its wire name.
    /// </summary>
    /// <param name="value">The internal code.</param>
    /// <returns>The wire code.</returns>
    /// <remarks>
    /// Written out in full rather than cast, so that adding an internal value without deciding how
    /// to publish it is a compile-time-adjacent failure rather than a silent numeric passthrough.
    /// A cast would have made every future code "work" and mean nothing.
    /// </remarks>
    public static RemoteAccessFindingCode ToWireCode(RemoteAccessDiagnosticCode value) => value switch
    {
        RemoteAccessDiagnosticCode.None => RemoteAccessFindingCode.None,
        RemoteAccessDiagnosticCode.SecureBootstrapActive => RemoteAccessFindingCode.SecureBootstrapActive,
        RemoteAccessDiagnosticCode.SecureBootstrapInactive => RemoteAccessFindingCode.SecureBootstrapInactive,
        RemoteAccessDiagnosticCode.BackendLoopbackOnly => RemoteAccessFindingCode.BackendLoopbackOnly,
        RemoteAccessDiagnosticCode.BackendWildcardBound => RemoteAccessFindingCode.BackendWildcardBound,
        RemoteAccessDiagnosticCode.BackendLanBound => RemoteAccessFindingCode.BackendLanBound,
        RemoteAccessDiagnosticCode.BackendUnixSocketConfigured => RemoteAccessFindingCode.BackendUnixSocketConfigured,
        RemoteAccessDiagnosticCode.BackendExposureUnknown => RemoteAccessFindingCode.BackendExposureUnknown,
        RemoteAccessDiagnosticCode.BackendGloballyRoutableBound => RemoteAccessFindingCode.BackendGloballyRoutableBound,
        RemoteAccessDiagnosticCode.BackendStructurallyConstrained => RemoteAccessFindingCode.BackendStructurallyConstrained,
        RemoteAccessDiagnosticCode.BackendPotentiallyPublic => RemoteAccessFindingCode.BackendPotentiallyPublic,
        RemoteAccessDiagnosticCode.ListenerObservedOnPort80 => RemoteAccessFindingCode.ListenerObservedOnPort80,
        RemoteAccessDiagnosticCode.NoListenerObservedOnPort80 => RemoteAccessFindingCode.NoListenerObservedOnPort80,
        RemoteAccessDiagnosticCode.ListenerObservedOnPort443 => RemoteAccessFindingCode.ListenerObservedOnPort443,
        RemoteAccessDiagnosticCode.NoListenerObservedOnPort443 => RemoteAccessFindingCode.NoListenerObservedOnPort443,
        RemoteAccessDiagnosticCode.ListenerInspectionDenied => RemoteAccessFindingCode.ListenerInspectionDenied,
        RemoteAccessDiagnosticCode.PossibleExistingIngressOwner => RemoteAccessFindingCode.PossibleExistingIngressOwner,
        RemoteAccessDiagnosticCode.ListenerInspectionUnsupported => RemoteAccessFindingCode.ListenerInspectionUnsupported,
        RemoteAccessDiagnosticCode.ListenerInspectionFailed => RemoteAccessFindingCode.ListenerInspectionFailed,
        RemoteAccessDiagnosticCode.KnownProxiesAbsent => RemoteAccessFindingCode.KnownProxiesAbsent,
        RemoteAccessDiagnosticCode.KnownProxiesMalformed => RemoteAccessFindingCode.KnownProxiesMalformed,
        RemoteAccessDiagnosticCode.MultipleKnownProxiesConfigured => RemoteAccessFindingCode.MultipleKnownProxiesConfigured,
        RemoteAccessDiagnosticCode.ForwardedHeadersDisabled => RemoteAccessFindingCode.ForwardedHeadersDisabled,
        RemoteAccessDiagnosticCode.ForwardedHeadersEnabledConsistently => RemoteAccessFindingCode.ForwardedHeadersEnabledConsistently,
        RemoteAccessDiagnosticCode.ForwardedHeaderTrustInconsistent => RemoteAccessFindingCode.ForwardedHeaderTrustInconsistent,
        RemoteAccessDiagnosticCode.SameHostProxyLoopbackTrustTrapPossible => RemoteAccessFindingCode.SameHostProxyLoopbackTrustTrapPossible,
        RemoteAccessDiagnosticCode.SingleKnownProxyNormalized => RemoteAccessFindingCode.SingleKnownProxyNormalized,
        RemoteAccessDiagnosticCode.HostnameNotProvided => RemoteAccessFindingCode.HostnameNotProvided,
        RemoteAccessDiagnosticCode.HostnameSyntacticallyInvalid => RemoteAccessFindingCode.HostnameSyntacticallyInvalid,
        RemoteAccessDiagnosticCode.DnsLookupSucceeded => RemoteAccessFindingCode.DnsLookupSucceeded,
        RemoteAccessDiagnosticCode.DnsNoAddressRecords => RemoteAccessFindingCode.DnsNoAddressRecords,
        RemoteAccessDiagnosticCode.DnsLookupTimedOut => RemoteAccessFindingCode.DnsLookupTimedOut,
        RemoteAccessDiagnosticCode.DnsLookupFailed => RemoteAccessFindingCode.DnsLookupFailed,
        RemoteAccessDiagnosticCode.DnsLookupCancelled => RemoteAccessFindingCode.DnsLookupCancelled,
        RemoteAccessDiagnosticCode.DnsResultContainsIPv4 => RemoteAccessFindingCode.DnsResultContainsIPv4,
        RemoteAccessDiagnosticCode.DnsResultContainsIPv6 => RemoteAccessFindingCode.DnsResultContainsIPv6,
        RemoteAccessDiagnosticCode.DnsAddressMatchesLocalGlobalAddress => RemoteAccessFindingCode.DnsAddressMatchesLocalGlobalAddress,
        RemoteAccessDiagnosticCode.DnsAddressMatchesNoLocalAddress => RemoteAccessFindingCode.DnsAddressMatchesNoLocalAddress,
        RemoteAccessDiagnosticCode.PrivateAddressingObserved => RemoteAccessFindingCode.PrivateAddressingObserved,
        RemoteAccessDiagnosticCode.SharedAddressSpaceObserved => RemoteAccessFindingCode.SharedAddressSpaceObserved,
        RemoteAccessDiagnosticCode.DirectPublicAddressPossible => RemoteAccessFindingCode.DirectPublicAddressPossible,
        RemoteAccessDiagnosticCode.NatOrUpstreamProxyPossiblyRequired => RemoteAccessFindingCode.NatOrUpstreamProxyPossiblyRequired,
        RemoteAccessDiagnosticCode.CgNatSignalObserved => RemoteAccessFindingCode.CgNatSignalObserved,
        RemoteAccessDiagnosticCode.CgNatNotDeterminable => RemoteAccessFindingCode.CgNatNotDeterminable,
        RemoteAccessDiagnosticCode.IpFamilyPolicyContradicted => RemoteAccessFindingCode.IpFamilyPolicyContradicted,
        RemoteAccessDiagnosticCode.IpFamilyPolicyUnresolved => RemoteAccessFindingCode.IpFamilyPolicyUnresolved,
        RemoteAccessDiagnosticCode.ExternalReachabilityUnverified => RemoteAccessFindingCode.ExternalReachabilityUnverified,
        RemoteAccessDiagnosticCode.CertificateReadinessUnverified => RemoteAccessFindingCode.CertificateReadinessUnverified,
        RemoteAccessDiagnosticCode.FirewallStateUnknown => RemoteAccessFindingCode.FirewallStateUnknown,
        RemoteAccessDiagnosticCode.RouterMappingUnknown => RemoteAccessFindingCode.RouterMappingUnknown,
        _ => throw new InvalidOperationException($"Diagnostic code '{value}' has no intentional wire disposition.")
    };
}
