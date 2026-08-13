using System;
using System.Collections.Generic;

namespace Tesserafin.Server.PublicExposure;

/// <summary>
/// Decides whether a server may be published to the Internet, from evidence alone.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator is a pure function of its argument. It has no constructor, no dependencies, no
/// configuration and no I/O: it cannot resolve a name, open a socket, start a service or write a
/// file, because it holds nothing that could. That is the design, not an omission — a readiness
/// check that probes is a readiness check that can be made to say yes by whatever it probed.
/// </para>
/// <para>
/// It answers one question and refuses several others. A remote-access boolean being on, a socket
/// being open, a local health endpoint answering 200, the wizard being finished, and a proxy
/// configuration file existing on disk are each, individually and together, not readiness. None of
/// them appears in <see cref="PublicExposureEvidence"/>, so none of them can be mistaken for it.
/// </para>
/// <para>
/// Every unestablished fact blocks. See tesserafin-project/tesserafin#242.
/// </para>
/// </remarks>
public static class PublicExposureReadinessEvaluator
{
    /// <summary>
    /// Evaluates an evidence record.
    /// </summary>
    /// <remarks>
    /// Deterministic: the same record always yields the same blockers in the same order. Blockers
    /// are collected exhaustively rather than short-circuited, so an operator sees everything that
    /// is wrong in one pass.
    /// </remarks>
    /// <param name="evidence">The evidence to judge.</param>
    /// <returns>The readiness result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="evidence"/> is <c>null</c>.</exception>
    public static PublicExposureReadiness Evaluate(PublicExposureEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        // An unrecognised schema is not a set of unestablished facts — it is a record this build
        // cannot read. Deriving further blockers from fields it may not have understood would be
        // inventing findings, so this is reported alone.
        if (evidence.SchemaVersion != PublicExposureEvidence.CurrentSchemaVersion)
        {
            return new PublicExposureReadiness(new[] { PublicExposureBlocker.EvidenceVersionUnsupported });
        }

        var blockers = new List<PublicExposureBlocker>();

        if (evidence.SetupWizardCompleted != true)
        {
            blockers.Add(PublicExposureBlocker.SetupWizardIncomplete);
        }

        if (evidence.EnabledAdministratorExists != true)
        {
            blockers.Add(PublicExposureBlocker.NoEnabledAdministrator);
        }

        if (evidence.AdministratorPasswordSet != true)
        {
            blockers.Add(PublicExposureBlocker.AdministratorPasswordAbsent);
        }

        if (evidence.AdministratorCredentialPolicySatisfied != true)
        {
            blockers.Add(PublicExposureBlocker.AdministratorCredentialPolicyUnresolved);
        }

        if (evidence.BackendTransportConstrained != true)
        {
            blockers.Add(PublicExposureBlocker.BackendTransportNotConstrained);
        }

        var proxyIdentity = evidence.ManagedProxyIdentity;
        var hasProxyIdentity = !string.IsNullOrWhiteSpace(proxyIdentity);
        if (!hasProxyIdentity)
        {
            blockers.Add(PublicExposureBlocker.ManagedProxyIdentityAbsent);
        }

        var knownProxies = evidence.KnownProxies;
        if (knownProxies is null || knownProxies.Count == 0)
        {
            blockers.Add(PublicExposureBlocker.KnownProxiesAbsent);
        }
        else if (knownProxies.Count > 1)
        {
            // More than one trusted peer is a boundary R0-B cannot validate: which of them the
            // forwarded headers are believed from decides who counts as a local client.
            blockers.Add(PublicExposureBlocker.KnownProxiesAmbiguous);
        }
        else if (hasProxyIdentity && !string.Equals(knownProxies[0], proxyIdentity, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(PublicExposureBlocker.KnownProxiesInconsistentWithProxyIdentity);
        }

        if (evidence.ForwardedHeadersTrustProxyIdentity != true)
        {
            blockers.Add(PublicExposureBlocker.ForwardedHeaderPostureInconsistent);
        }

        if (string.IsNullOrWhiteSpace(evidence.PublicHostname))
        {
            blockers.Add(PublicExposureBlocker.PublicHostnameAbsent);
        }

        if (evidence.CertificateReady != true)
        {
            blockers.Add(PublicExposureBlocker.CertificateReadinessUnproven);
        }

        if (evidence.ExternalReachabilityVerified != true)
        {
            blockers.Add(PublicExposureBlocker.ExternalReachabilityUnproven);
        }

        AddIpPolicyBlockers(evidence, blockers);

        if (evidence.PublicAccessEnabled != true)
        {
            blockers.Add(PublicExposureBlocker.PublicAccessDisabled);
        }

        // Disabled and externally answering at the same time. Both cannot be true; a fail-closed
        // evaluator does not choose which one to believe.
        if (evidence.PublicAccessEnabled == false && evidence.ExternalReachabilityVerified == true)
        {
            blockers.Add(PublicExposureBlocker.EvidenceContradictory);
        }

        return new PublicExposureReadiness(blockers);
    }

    private static void AddIpPolicyBlockers(PublicExposureEvidence evidence, List<PublicExposureBlocker> blockers)
    {
        var v4Policy = evidence.IPv4PolicyEnabled;
        var v6Policy = evidence.IPv6PolicyEnabled;

        if (v4Policy is null || v6Policy is null || (v4Policy == false && v6Policy == false))
        {
            blockers.Add(PublicExposureBlocker.IpPolicyUnresolved);
            return;
        }

        // A family that policy includes must be verified; a family policy excludes must not answer.
        // The second half is the one that matters: an operator who publishes IPv4 and forgets the
        // IPv6 firewall has a reachable server they believe is closed.
        var v4Disagrees = (v4Policy == true && evidence.IPv4ReachabilityVerified != true)
            || (v4Policy == false && evidence.IPv4ReachabilityVerified == true);
        var v6Disagrees = (v6Policy == true && evidence.IPv6ReachabilityVerified != true)
            || (v6Policy == false && evidence.IPv6ReachabilityVerified == true);

        if (v4Disagrees || v6Disagrees)
        {
            blockers.Add(PublicExposureBlocker.IpPolicyContradicted);
        }
    }
}
