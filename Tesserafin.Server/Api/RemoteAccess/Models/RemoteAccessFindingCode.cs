namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// The diagnostic vocabulary, as the contract spells it.
/// </summary>
/// <remarks>
/// This mirrors the internal <c>RemoteAccessDiagnosticCode</c> one-for-one and exists so that the
/// published contract is not the internal enum. The two are kept in lockstep by an exhaustive
/// two-directional mapping test: adding an internal value without an intentional disposition here
/// fails the build rather than silently disappearing from every client.
///
/// The names are the contract. They are what an operator-facing surface renders and what
/// translations key off, so renaming one is a contract change and has to be visible in review.
/// Values are serialised as NAMES, not CLR numbers — <c>JsonStringEnumConverter</c> is registered
/// globally today, and a test pins that rather than relying on it.
/// </remarks>
public enum RemoteAccessFindingCode
{
    /// <summary>Reserved. Never emitted from a valid report, and never means success.</summary>
    None = 0,

    /// <summary>Secure bootstrap is active.</summary>
    SecureBootstrapActive = 1,

    /// <summary>Secure bootstrap is not active.</summary>
    SecureBootstrapInactive = 2,

    /// <summary>The backend is bound to loopback only.</summary>
    BackendLoopbackOnly = 10,

    /// <summary>The backend is bound to a wildcard address.</summary>
    BackendWildcardBound = 11,

    /// <summary>The backend is bound to LAN addresses.</summary>
    BackendLanBound = 12,

    /// <summary>A unix socket is configured.</summary>
    BackendUnixSocketConfigured = 13,

    /// <summary>The backend's exposure could not be determined.</summary>
    BackendExposureUnknown = 14,

    /// <summary>The backend is bound to a globally routable address.</summary>
    BackendGloballyRoutableBound = 15,

    /// <summary>The backend's binding is structurally constrained.</summary>
    BackendStructurallyConstrained = 16,

    /// <summary>The backend may be reachable from outside the host.</summary>
    BackendPotentiallyPublic = 17,

    /// <summary>A listener was observed on port 80.</summary>
    ListenerObservedOnPort80 = 20,

    /// <summary>No listener was observed on port 80.</summary>
    NoListenerObservedOnPort80 = 21,

    /// <summary>A listener was observed on port 443.</summary>
    ListenerObservedOnPort443 = 22,

    /// <summary>No listener was observed on port 443.</summary>
    NoListenerObservedOnPort443 = 23,

    /// <summary>Listener inspection was denied by the operating system.</summary>
    ListenerInspectionDenied = 24,

    /// <summary>Something else may already own ingress on this host.</summary>
    PossibleExistingIngressOwner = 25,

    /// <summary>Listener inspection is unsupported on this platform.</summary>
    ListenerInspectionUnsupported = 26,

    /// <summary>Listener inspection failed.</summary>
    ListenerInspectionFailed = 27,

    /// <summary>No known proxies are configured.</summary>
    KnownProxiesAbsent = 30,

    /// <summary>A configured known proxy could not be parsed.</summary>
    KnownProxiesMalformed = 31,

    /// <summary>More than one known proxy is configured.</summary>
    MultipleKnownProxiesConfigured = 32,

    /// <summary>Forwarded headers are disabled.</summary>
    ForwardedHeadersDisabled = 33,

    /// <summary>Forwarded headers are enabled consistently with the configured proxies.</summary>
    ForwardedHeadersEnabledConsistently = 34,

    /// <summary>Forwarded-header trust is inconsistent with the configured proxies.</summary>
    ForwardedHeaderTrustInconsistent = 35,

    /// <summary>A same-host proxy may create a loopback trust trap.</summary>
    SameHostProxyLoopbackTrustTrapPossible = 36,

    /// <summary>Exactly one known proxy is configured and normalised.</summary>
    SingleKnownProxyNormalized = 37,

    /// <summary>No hostname was provided.</summary>
    HostnameNotProvided = 40,

    /// <summary>The provided hostname is not syntactically a hostname.</summary>
    HostnameSyntacticallyInvalid = 41,

    /// <summary>The DNS lookup returned addresses.</summary>
    DnsLookupSucceeded = 42,

    /// <summary>The DNS lookup returned no address records.</summary>
    DnsNoAddressRecords = 43,

    /// <summary>The DNS lookup timed out.</summary>
    DnsLookupTimedOut = 44,

    /// <summary>The DNS lookup failed.</summary>
    DnsLookupFailed = 45,

    /// <summary>The DNS lookup was cancelled by the caller.</summary>
    DnsLookupCancelled = 46,

    /// <summary>The DNS result contains at least one IPv4 address.</summary>
    DnsResultContainsIPv4 = 47,

    /// <summary>The DNS result contains at least one IPv6 address.</summary>
    DnsResultContainsIPv6 = 48,

    /// <summary>A resolved address matches a globally routable local address.</summary>
    DnsAddressMatchesLocalGlobalAddress = 49,

    /// <summary>No resolved address matches any local address.</summary>
    DnsAddressMatchesNoLocalAddress = 50,

    /// <summary>Private addressing was observed.</summary>
    PrivateAddressingObserved = 60,

    /// <summary>Shared address space (RFC 6598) was observed.</summary>
    SharedAddressSpaceObserved = 61,

    /// <summary>A direct public address may be available.</summary>
    DirectPublicAddressPossible = 62,

    /// <summary>NAT or an upstream proxy may be required.</summary>
    NatOrUpstreamProxyPossiblyRequired = 63,

    /// <summary>A carrier-grade NAT signal was observed.</summary>
    CgNatSignalObserved = 64,

    /// <summary>Carrier-grade NAT could not be determined.</summary>
    CgNatNotDeterminable = 65,

    /// <summary>The stated IP-family policy contradicts what was observed.</summary>
    IpFamilyPolicyContradicted = 66,

    /// <summary>The stated IP-family policy could not be resolved against observations.</summary>
    IpFamilyPolicyUnresolved = 67,

    /// <summary>Permanently unknown: nothing here can verify external reachability.</summary>
    ExternalReachabilityUnverified = 90,

    /// <summary>Permanently unknown: nothing here can verify certificate readiness.</summary>
    CertificateReadinessUnverified = 91,

    /// <summary>Permanently unknown: nothing here can observe firewall state.</summary>
    FirewallStateUnknown = 92,

    /// <summary>Permanently unknown: nothing here can observe router mapping.</summary>
    RouterMappingUnknown = 93
}
