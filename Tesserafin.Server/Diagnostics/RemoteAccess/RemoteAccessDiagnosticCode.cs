namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// The stable vocabulary of things this layer can say.
/// </summary>
/// <remarks>
/// <para>
/// These names are contract. They are what a future operator-facing surface renders, what its
/// translations key off, and what its tests assert against, so they are serialized by name and
/// pinned by a test rather than left to enum ordering.
/// </para>
/// <para>
/// Note what is absent: there is no <c>Healthy</c>, no <c>Secure</c>, no <c>Ready</c>, no
/// <c>Working</c> and no <c>Reachable</c>. Those words describe conclusions this layer is not
/// entitled to reach, and leaving them out of the vocabulary is cheaper than policing their use.
/// </para>
/// </remarks>
public enum RemoteAccessDiagnosticCode
{
    /// <summary>
    /// Not a code. The default value of the type, never emitted.
    /// </summary>
    None = 0,

    /// <summary>Secure bootstrap mode is active for this process.</summary>
    SecureBootstrapActive = 1,

    /// <summary>Secure bootstrap mode is not active for this process.</summary>
    SecureBootstrapInactive = 2,

    /// <summary>Every configured listener resolves to a loopback address.</summary>
    BackendLoopbackOnly = 10,

    /// <summary>A configured listener resolves to a wildcard address, so it accepts on every interface.</summary>
    BackendWildcardBound = 11,

    /// <summary>Listeners are bound to explicit private or link-local addresses.</summary>
    BackendLanBound = 12,

    /// <summary>A Unix domain socket is configured for the backend.</summary>
    BackendUnixSocketConfigured = 13,

    /// <summary>The effective bind posture could not be determined.</summary>
    BackendExposureUnknown = 14,

    /// <summary>
    /// Listeners are bound to explicit globally routable addresses. Kept apart from
    /// <see cref="BackendLanBound"/> because binding a public address is a different decision from
    /// binding a LAN one, and reporting both as "explicit" would hide the difference.
    /// </summary>
    BackendGloballyRoutableBound = 15,

    /// <summary>
    /// The backend cannot itself accept a connection arriving from off-host. This is a statement
    /// about the socket only; it is not a statement that the host, the name or the path to it is
    /// secure, and nothing about the firewall or an on-host proxy follows from it.
    /// </summary>
    BackendStructurallyConstrained = 16,

    /// <summary>
    /// The backend's own socket would accept a connection arriving from off-host. Whether one can
    /// arrive is a firewall and routing question this layer cannot answer.
    /// </summary>
    BackendPotentiallyPublic = 17,

    /// <summary>A TCP listener was observed on port 80.</summary>
    ListenerObservedOnPort80 = 20,

    /// <summary>No TCP listener was observed on port 80. This is not a statement that the port is free.</summary>
    NoListenerObservedOnPort80 = 21,

    /// <summary>A TCP listener was observed on port 443.</summary>
    ListenerObservedOnPort443 = 22,

    /// <summary>No TCP listener was observed on port 443. This is not a statement that the port is free.</summary>
    NoListenerObservedOnPort443 = 23,

    /// <summary>The operating system refused the listener listing.</summary>
    ListenerInspectionDenied = 24,

    /// <summary>Something is already listening on 80 or 443. Which product it is, is not knowable from here.</summary>
    PossibleExistingIngressOwner = 25,

    /// <summary>This platform offers no read-only way to list listeners.</summary>
    ListenerInspectionUnsupported = 26,

    /// <summary>
    /// The listing failed for a reason that is neither denial nor lack of support. Kept apart from
    /// the other two because an operator can act on a denial and on a missing platform facility,
    /// and can act on neither if all three arrive under one name.
    /// </summary>
    ListenerInspectionFailed = 27,

    /// <summary>No known proxy is configured, so forwarded headers are discarded entirely.</summary>
    KnownProxiesAbsent = 30,

    /// <summary>At least one configured known-proxy entry could not be parsed.</summary>
    KnownProxiesMalformed = 31,

    /// <summary>More than one known proxy is configured.</summary>
    MultipleKnownProxiesConfigured = 32,

    /// <summary>Forwarded-header processing is off.</summary>
    ForwardedHeadersDisabled = 33,

    /// <summary>Forwarded-header processing is on and agrees with the configured trusted peers.</summary>
    ForwardedHeadersEnabledConsistently = 34,

    /// <summary>Forwarded-header processing and the configured trusted peers do not agree.</summary>
    ForwardedHeaderTrustInconsistent = 35,

    /// <summary>
    /// A same-host proxy could make every remote visitor arrive as loopback and be classified as
    /// local. See tesserafin-project/tesserafin#241 §1.5.
    /// </summary>
    SameHostProxyLoopbackTrustTrapPossible = 36,

    /// <summary>Exactly one known proxy is configured and the server's own parser accepted it.</summary>
    SingleKnownProxyNormalized = 37,

    /// <summary>No hostname was supplied, so nothing about naming could be examined.</summary>
    HostnameNotProvided = 40,

    /// <summary>The supplied hostname is not a hostname this layer will resolve.</summary>
    HostnameSyntacticallyInvalid = 41,

    /// <summary>The resolver answered with at least one address.</summary>
    DnsLookupSucceeded = 42,

    /// <summary>The resolver answered, with no A or AAAA record.</summary>
    DnsNoAddressRecords = 43,

    /// <summary>The lookup exceeded its bounded deadline.</summary>
    DnsLookupTimedOut = 44,

    /// <summary>The resolver reported a failure.</summary>
    DnsLookupFailed = 45,

    /// <summary>The caller cancelled before the lookup completed.</summary>
    DnsLookupCancelled = 46,

    /// <summary>The answer set contains at least one IPv4 address.</summary>
    DnsResultContainsIPv4 = 47,

    /// <summary>The answer set contains at least one IPv6 address.</summary>
    DnsResultContainsIPv6 = 48,

    /// <summary>An answer matches a globally routable address observed on this host.</summary>
    DnsAddressMatchesLocalGlobalAddress = 49,

    /// <summary>No answer matches any address observed on this host.</summary>
    DnsAddressMatchesNoLocalAddress = 50,

    /// <summary>An address in RFC 1918 or IPv6 unique-local space was observed on this host.</summary>
    PrivateAddressingObserved = 60,

    /// <summary>An address in RFC 6598 shared address space (100.64.0.0/10) was observed on this host.</summary>
    SharedAddressSpaceObserved = 61,

    /// <summary>The hostname may resolve straight to this host. Firewall passage remains unknown.</summary>
    DirectPublicAddressPossible = 62,

    /// <summary>Reaching this host from outside would require NAT or an upstream proxy.</summary>
    NatOrUpstreamProxyPossiblyRequired = 63,

    /// <summary>Shared address space is present, which is consistent with carrier-grade NAT.</summary>
    CgNatSignalObserved = 64,

    /// <summary>Nothing observed here settles whether carrier-grade NAT is in the path.</summary>
    CgNatNotDeterminable = 65,

    /// <summary>The declared IPv4 and IPv6 publication policy is not satisfiable by what was observed.</summary>
    IpFamilyPolicyContradicted = 66,

    /// <summary>
    /// The caller did not state a publication policy for one or both families. Unstated is its own
    /// answer here; it must never be read as "no", because a family nobody decided about is exactly
    /// the one whose firewall nobody checked.
    /// </summary>
    IpFamilyPolicyUnresolved = 67,

    /// <summary>Whether anything outside this host can reach it was not established, and cannot be from here.</summary>
    ExternalReachabilityUnverified = 90,

    /// <summary>Whether a trusted certificate exists and renews was not established.</summary>
    CertificateReadinessUnverified = 91,

    /// <summary>Host and network firewall state was not established.</summary>
    FirewallStateUnknown = 92,

    /// <summary>Router or NAT port mapping was not established.</summary>
    RouterMappingUnknown = 93
}
