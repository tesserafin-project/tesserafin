using System.Collections.Generic;

namespace Tesserafin.Server.PublicExposure;

/// <summary>
/// Everything a future managed-publication slice would have to KNOW before it could publish a
/// server, expressed as a record with no behaviour.
/// </summary>
/// <remarks>
/// <para>
/// Every fact is nullable on purpose, and <c>null</c> means "not established". It is never read as
/// "no": <see cref="PublicExposureReadinessEvaluator"/> treats an unestablished fact exactly as it
/// treats a negative one, which is what makes the evaluator fail closed rather than fail silent.
/// </para>
/// <para>
/// R0-B ships no producer for this record. Nothing in the server constructs one from live state,
/// and two of its fields — <see cref="AdministratorCredentialPolicySatisfied"/> and
/// <see cref="ExternalReachabilityVerified"/> — could not be produced honestly even if it did,
/// because decisions D-5 and D-8 of the parent RFC are open. Tests build records by hand; that is
/// the only caller today, and it is how the evaluator can be shown to be genuinely evidence-driven
/// rather than hardcoded to refuse.
/// </para>
/// </remarks>
public sealed record PublicExposureEvidence
{
    /// <summary>
    /// The evidence schema this build understands.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Gets the schema version of this record.
    /// </summary>
    /// <remarks>
    /// Defaults to zero, so a default-constructed or partially deserialized record is rejected
    /// outright instead of being read as a set of unestablished facts.
    /// </remarks>
    public int SchemaVersion { get; init; }

    /// <summary>
    /// Gets a value indicating whether the first-run wizard has been completed.
    /// </summary>
    public bool? SetupWizardCompleted { get; init; }

    /// <summary>
    /// Gets a value indicating whether at least one enabled administrator account exists.
    /// </summary>
    public bool? EnabledAdministratorExists { get; init; }

    /// <summary>
    /// Gets a value indicating whether that administrator has a password set.
    /// </summary>
    public bool? AdministratorPasswordSet { get; init; }

    /// <summary>
    /// Gets a value indicating whether the administrator credential satisfies the agreed strength
    /// floor. No producer exists while decision D-5 is open.
    /// </summary>
    public bool? AdministratorCredentialPolicySatisfied { get; init; }

    /// <summary>
    /// Gets a value indicating whether the application's own listeners are confined to a transport
    /// that is not publicly reachable.
    /// </summary>
    public bool? BackendTransportConstrained { get; init; }

    /// <summary>
    /// Gets the address of the single proxy the server is to trust forwarded headers from.
    /// </summary>
    public string? ManagedProxyIdentity { get; init; }

    /// <summary>
    /// Gets the configured known-proxy list.
    /// </summary>
    public IReadOnlyList<string>? KnownProxies { get; init; }

    /// <summary>
    /// Gets a value indicating whether forwarded-header handling is aligned with
    /// <see cref="ManagedProxyIdentity"/>.
    /// </summary>
    public bool? ForwardedHeadersTrustProxyIdentity { get; init; }

    /// <summary>
    /// Gets the public hostname the server would be published under.
    /// </summary>
    public string? PublicHostname { get; init; }

    /// <summary>
    /// Gets a value indicating whether a trusted certificate for that hostname is present and
    /// renewing.
    /// </summary>
    public bool? CertificateReady { get; init; }

    /// <summary>
    /// Gets a value indicating whether an independent check confirmed the host answers from
    /// outside. No producer exists while decision D-8 is open.
    /// </summary>
    public bool? ExternalReachabilityVerified { get; init; }

    /// <summary>
    /// Gets a value indicating whether the operator currently wants public access at all.
    /// </summary>
    public bool? PublicAccessEnabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether IPv4 is part of the declared publication policy.
    /// </summary>
    public bool? IPv4PolicyEnabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether IPv6 is part of the declared publication policy.
    /// </summary>
    public bool? IPv6PolicyEnabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether IPv4 reachability was independently verified.
    /// </summary>
    public bool? IPv4ReachabilityVerified { get; init; }

    /// <summary>
    /// Gets a value indicating whether IPv6 reachability was independently verified.
    /// </summary>
    public bool? IPv6ReachabilityVerified { get; init; }
}
