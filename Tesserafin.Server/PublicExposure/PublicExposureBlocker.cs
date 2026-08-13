namespace Tesserafin.Server.PublicExposure;

/// <summary>
/// A single, named reason why a server must not be published to the Internet.
/// </summary>
/// <remarks>
/// These codes are the vocabulary a future managed-publication slice reports to an operator, and
/// the vocabulary its tests assert against. They describe MISSING OR CONTRADICTORY EVIDENCE, never
/// an attempt that failed: the evaluator that produces them talks to nothing.
/// </remarks>
public enum PublicExposureBlocker
{
    /// <summary>
    /// Not a blocker. The default value of the type, never emitted by
    /// <see cref="PublicExposureReadinessEvaluator"/>; a readiness result containing it is a
    /// programming error, not a finding.
    /// </summary>
    None = 0,

    /// <summary>
    /// The evidence record's schema version is not one this build understands, so nothing in it can
    /// be interpreted. Includes the default-constructed record, whose version is zero.
    /// </summary>
    EvidenceVersionUnsupported = 1,

    /// <summary>
    /// The first-run wizard has not been completed, or the evidence does not say.
    /// </summary>
    SetupWizardIncomplete = 2,

    /// <summary>
    /// No enabled administrator account exists, or the evidence does not say.
    /// </summary>
    NoEnabledAdministrator = 3,

    /// <summary>
    /// The administrator has no password set, or the evidence does not say.
    /// </summary>
    AdministratorPasswordAbsent = 4,

    /// <summary>
    /// The administrator credential meets no agreed strength floor. Unresolved by design: decision
    /// D-5 of the parent RFC has not been taken, so nothing in the product can produce this
    /// evidence and this blocker cannot currently be cleared at runtime.
    /// </summary>
    AdministratorCredentialPolicyUnresolved = 5,

    /// <summary>
    /// The application's own listeners are not confined to a non-public transport, or the evidence
    /// does not say. A backend reachable directly defeats the proxy entirely.
    /// </summary>
    BackendTransportNotConstrained = 6,

    /// <summary>
    /// No managed proxy identity is declared, so there is nothing for the trust boundary to name.
    /// </summary>
    ManagedProxyIdentityAbsent = 7,

    /// <summary>
    /// The known-proxy list is empty or absent. With no known proxy the server discards
    /// <c>X-Forwarded-*</c> entirely and every proxied request keeps the proxy's own source
    /// address, which on a same-host proxy means every visitor is classified as local.
    /// </summary>
    KnownProxiesAbsent = 8,

    /// <summary>
    /// The known-proxy list names more than one peer, so which one the forwarded headers are
    /// trusted from is ambiguous. R0-B can only validate a single declared proxy identity.
    /// </summary>
    KnownProxiesAmbiguous = 9,

    /// <summary>
    /// The known-proxy list does not name the declared managed proxy.
    /// </summary>
    KnownProxiesInconsistentWithProxyIdentity = 10,

    /// <summary>
    /// Forwarded-header handling is not aligned with the declared proxy identity, or the evidence
    /// does not say.
    /// </summary>
    ForwardedHeaderPostureInconsistent = 11,

    /// <summary>
    /// No public hostname is declared.
    /// </summary>
    PublicHostnameAbsent = 12,

    /// <summary>
    /// A trusted certificate for the declared hostname is not proven present and renewing.
    /// </summary>
    CertificateReadinessUnproven = 13,

    /// <summary>
    /// Reachability has not been confirmed from outside the host, or confirmation failed.
    /// Unresolved by design: decision D-8 of the parent RFC has not been taken, so nothing in the
    /// product can produce this evidence and this blocker cannot currently be cleared at runtime.
    /// </summary>
    ExternalReachabilityUnproven = 14,

    /// <summary>
    /// The IPv4/IPv6 publication policy is not stated.
    /// </summary>
    IpPolicyUnresolved = 15,

    /// <summary>
    /// Verified reachability and the declared IPv4/IPv6 policy disagree — typically a family that
    /// policy excludes answering anyway, which is a firewall the operator does not have.
    /// </summary>
    IpPolicyContradicted = 16,

    /// <summary>
    /// The operator has explicitly disabled public access, or the evidence does not say.
    /// </summary>
    PublicAccessDisabled = 17,

    /// <summary>
    /// The evidence asserts both that public access is disabled and that the host answers from
    /// outside. One of the two is wrong, and a fail-closed evaluator may not guess which.
    /// </summary>
    EvidenceContradictory = 18
}
