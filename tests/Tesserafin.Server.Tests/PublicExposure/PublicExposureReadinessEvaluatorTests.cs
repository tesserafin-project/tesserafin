using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Tesserafin.Server.PublicExposure;
using Xunit;

namespace Tesserafin.Server.Tests.PublicExposure;

/// <summary>
/// The fail-closed publication-readiness evaluator.
/// </summary>
/// <remarks>
/// The evaluator's whole job is to say no for a nameable reason. These tests check that it says no
/// for the RIGHT reason, that it says no when it was told nothing, and — through
/// <see cref="Ready"/> — that it is genuinely evidence-driven rather than hardcoded to refuse,
/// which is the only way its refusals mean anything.
/// </remarks>
public sealed class PublicExposureReadinessEvaluatorTests
{
    private const string ProxyIdentity = "127.0.0.1";

    /// <summary>
    /// The synthetic, fully satisfied evidence set.
    /// </summary>
    /// <remarks>
    /// No code in the server constructs this. Two of its fields could not be produced honestly even
    /// if it did — decisions D-5 (administrator credential floor) and D-8 (external reachability
    /// probe) are open — so real runtime readiness stays false regardless of anything else. It
    /// exists here so that every "blocked" assertion below is a one-field difference from a known
    /// ready state, rather than an assertion about a function that can only ever return false.
    /// </remarks>
    private static PublicExposureEvidence Ready() => new()
    {
        SchemaVersion = PublicExposureEvidence.CurrentSchemaVersion,
        SetupWizardCompleted = true,
        EnabledAdministratorExists = true,
        AdministratorPasswordSet = true,
        AdministratorCredentialPolicySatisfied = true,
        BackendTransportConstrained = true,
        ManagedProxyIdentity = ProxyIdentity,
        KnownProxies = new[] { ProxyIdentity },
        ForwardedHeadersTrustProxyIdentity = true,
        PublicHostname = "media.example.org",
        CertificateReady = true,
        ExternalReachabilityVerified = true,
        PublicAccessEnabled = true,
        IPv4PolicyEnabled = true,
        IPv6PolicyEnabled = false,
        IPv4ReachabilityVerified = true,
        IPv6ReachabilityVerified = false
    };

    [Fact]
    public void ASyntheticCompleteEvidenceSetIsReady()
    {
        var result = PublicExposureReadinessEvaluator.Evaluate(Ready());

        Assert.True(result.IsReady, $"Expected ready; blocked by: {string.Join(", ", result.Blockers)}");
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void ADefaultConstructedRecordIsRejectedOutright()
    {
        // Schema version zero. A partially deserialized or forgotten record is not a set of
        // unestablished facts, it is a record this build cannot read.
        var result = PublicExposureReadinessEvaluator.Evaluate(new PublicExposureEvidence());

        Assert.False(result.IsReady);
        Assert.Equal(new[] { PublicExposureBlocker.EvidenceVersionUnsupported }, result.Blockers);
    }

    [Fact]
    public void AFutureSchemaVersionIsRejectedOutright()
    {
        var result = PublicExposureReadinessEvaluator.Evaluate(
            Ready() with { SchemaVersion = PublicExposureEvidence.CurrentSchemaVersion + 1 });

        Assert.False(result.IsReady);
        Assert.Equal(new[] { PublicExposureBlocker.EvidenceVersionUnsupported }, result.Blockers);
    }

    [Fact]
    public void NullEvidenceThrows()
    {
        Assert.Throws<ArgumentNullException>(() => PublicExposureReadinessEvaluator.Evaluate(null!));
    }

    public static TheoryData<string, PublicExposureBlocker> MissingFacts() => new()
    {
        { nameof(PublicExposureEvidence.SetupWizardCompleted), PublicExposureBlocker.SetupWizardIncomplete },
        { nameof(PublicExposureEvidence.EnabledAdministratorExists), PublicExposureBlocker.NoEnabledAdministrator },
        { nameof(PublicExposureEvidence.AdministratorPasswordSet), PublicExposureBlocker.AdministratorPasswordAbsent },
        { nameof(PublicExposureEvidence.AdministratorCredentialPolicySatisfied), PublicExposureBlocker.AdministratorCredentialPolicyUnresolved },
        { nameof(PublicExposureEvidence.BackendTransportConstrained), PublicExposureBlocker.BackendTransportNotConstrained },
        { nameof(PublicExposureEvidence.ForwardedHeadersTrustProxyIdentity), PublicExposureBlocker.ForwardedHeaderPostureInconsistent },
        { nameof(PublicExposureEvidence.CertificateReady), PublicExposureBlocker.CertificateReadinessUnproven },
        { nameof(PublicExposureEvidence.ExternalReachabilityVerified), PublicExposureBlocker.ExternalReachabilityUnproven },
        { nameof(PublicExposureEvidence.PublicAccessEnabled), PublicExposureBlocker.PublicAccessDisabled }
    };

    [Theory]
    [MemberData(nameof(MissingFacts))]
    public void AnUnestablishedFactProducesItsOwnBlocker(string property, PublicExposureBlocker expected)
    {
        // null is "not established" and is treated exactly as "no". This is the fail-closed rule.
        var evidence = WithBoolean(Ready(), property, null);

        var result = PublicExposureReadinessEvaluator.Evaluate(evidence);

        Assert.False(result.IsReady);
        Assert.True(result.Has(expected), $"Expected {expected}; got {string.Join(", ", result.Blockers)}");
    }

    [Theory]
    [MemberData(nameof(MissingFacts))]
    public void ANegativeFactProducesTheSameBlockerAsAnUnestablishedOne(string property, PublicExposureBlocker expected)
    {
        var result = PublicExposureReadinessEvaluator.Evaluate(WithBoolean(Ready(), property, false));

        Assert.False(result.IsReady);
        Assert.True(result.Has(expected), $"Expected {expected}; got {string.Join(", ", result.Blockers)}");
    }

    [Fact]
    public void AnAbsentProxyIdentityBlocks()
    {
        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { ManagedProxyIdentity = null })
            .Has(PublicExposureBlocker.ManagedProxyIdentityAbsent));

        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { ManagedProxyIdentity = "   " })
            .Has(PublicExposureBlocker.ManagedProxyIdentityAbsent));
    }

    [Fact]
    public void AnEmptyKnownProxyListBlocks()
    {
        // The flagship misconfiguration. With no known proxy the server discards X-Forwarded-* and
        // every request through a same-host proxy keeps the proxy's own loopback source address,
        // so every visitor is classified as local.
        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { KnownProxies = Array.Empty<string>() })
            .Has(PublicExposureBlocker.KnownProxiesAbsent));

        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { KnownProxies = null })
            .Has(PublicExposureBlocker.KnownProxiesAbsent));
    }

    [Fact]
    public void MoreThanOneKnownProxyIsAmbiguousAndBlocks()
    {
        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { KnownProxies = new[] { ProxyIdentity, "10.0.0.5" } })
            .Has(PublicExposureBlocker.KnownProxiesAmbiguous));
    }

    [Fact]
    public void AKnownProxyThatIsNotTheDeclaredProxyBlocks()
    {
        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { KnownProxies = new[] { "10.0.0.5" } })
            .Has(PublicExposureBlocker.KnownProxiesInconsistentWithProxyIdentity));
    }

    [Fact]
    public void AnAbsentPublicHostnameBlocks()
    {
        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { PublicHostname = null })
            .Has(PublicExposureBlocker.PublicHostnameAbsent));

        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { PublicHostname = " " })
            .Has(PublicExposureBlocker.PublicHostnameAbsent));
    }

    [Fact]
    public void AnUnstatedIpPolicyBlocks()
    {
        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { IPv6PolicyEnabled = null })
            .Has(PublicExposureBlocker.IpPolicyUnresolved));

        Assert.True(PublicExposureReadinessEvaluator
            .Evaluate(Ready() with { IPv4PolicyEnabled = false, IPv6PolicyEnabled = false })
            .Has(PublicExposureBlocker.IpPolicyUnresolved));
    }

    [Fact]
    public void AFamilyThatPolicyExcludesButThatAnswersBlocks()
    {
        // The IPv6 bypass: an operator publishes IPv4, forgets the IPv6 firewall, and has a
        // reachable server they believe is closed.
        var result = PublicExposureReadinessEvaluator.Evaluate(
            Ready() with { IPv6PolicyEnabled = false, IPv6ReachabilityVerified = true });

        Assert.False(result.IsReady);
        Assert.True(result.Has(PublicExposureBlocker.IpPolicyContradicted));
    }

    [Fact]
    public void AFamilyThatPolicyIncludesButThatIsUnverifiedBlocks()
    {
        var result = PublicExposureReadinessEvaluator.Evaluate(
            Ready() with { IPv4ReachabilityVerified = null });

        Assert.False(result.IsReady);
        Assert.True(result.Has(PublicExposureBlocker.IpPolicyContradicted));
    }

    [Fact]
    public void DisabledAndExternallyReachableAtOnceIsContradictoryAndBlocks()
    {
        var result = PublicExposureReadinessEvaluator.Evaluate(
            Ready() with { PublicAccessEnabled = false, ExternalReachabilityVerified = true });

        Assert.False(result.IsReady);
        Assert.True(result.Has(PublicExposureBlocker.EvidenceContradictory));
        Assert.True(result.Has(PublicExposureBlocker.PublicAccessDisabled));
    }

    [Fact]
    public void EveryBlockerIsReportedAtOnce()
    {
        // Evaluation order must not suppress simultaneous findings: a caller told only the first
        // reason will fix it, ask again, and be told a second — which is how a half-configured
        // server ends up published.
        var evidence = Ready() with
        {
            SetupWizardCompleted = null,
            EnabledAdministratorExists = null,
            CertificateReady = null,
            PublicHostname = null,
            KnownProxies = null
        };

        var result = PublicExposureReadinessEvaluator.Evaluate(evidence);

        Assert.True(result.Has(PublicExposureBlocker.SetupWizardIncomplete));
        Assert.True(result.Has(PublicExposureBlocker.NoEnabledAdministrator));
        Assert.True(result.Has(PublicExposureBlocker.CertificateReadinessUnproven));
        Assert.True(result.Has(PublicExposureBlocker.PublicHostnameAbsent));
        Assert.True(result.Has(PublicExposureBlocker.KnownProxiesAbsent));
        Assert.True(result.Blockers.Count >= 5);
    }

    [Fact]
    public void WizardCompletionAloneIsNotReadiness()
    {
        var result = PublicExposureReadinessEvaluator.Evaluate(new PublicExposureEvidence
        {
            SchemaVersion = PublicExposureEvidence.CurrentSchemaVersion,
            SetupWizardCompleted = true
        });

        Assert.False(result.IsReady);
        Assert.True(result.Has(PublicExposureBlocker.ExternalReachabilityUnproven));
        Assert.True(result.Has(PublicExposureBlocker.KnownProxiesAbsent));
    }

    [Fact]
    public void TheEvidenceModelCannotExpressARemoteAccessBooleanOrAListeningSocketOrALocalHealthCheck()
    {
        // None of these is readiness, so none of them is in the model. If one is ever added, this
        // gate fails and the addition has to be argued for rather than slipped in.
        var forbidden = new[] { "EnableRemoteAccess", "RemoteAccess", "Listening", "SocketOpen", "LocalHealth", "HealthCheck", "CaddyfileExists" };
        var properties = typeof(PublicExposureEvidence)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(properties, p => p.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheEvaluatorHoldsNothingThatCouldReachTheNetworkOrTheFilesystem()
    {
        // The structural proof that evaluation performs no configuration write and no external
        // call: a static class with no state and one method whose only parameter is the record.
        var type = typeof(PublicExposureReadinessEvaluator);

        Assert.True(type.IsAbstract && type.IsSealed, "The evaluator must be a static class.");
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.DoesNotContain(
            type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            f => !f.IsLiteral);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var publicMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Single(publicMethods);
        Assert.Equal(nameof(PublicExposureReadinessEvaluator.Evaluate), publicMethods[0].Name);
        Assert.Equal(
            new[] { typeof(PublicExposureEvidence) },
            publicMethods[0].GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void EvaluationIsDeterministic()
    {
        var evidence = Ready() with { CertificateReady = null, KnownProxies = null };

        var first = PublicExposureReadinessEvaluator.Evaluate(evidence);
        var second = PublicExposureReadinessEvaluator.Evaluate(evidence);
        var third = PublicExposureReadinessEvaluator.Evaluate(evidence);

        Assert.Equal(first.Blockers, second.Blockers);
        Assert.Equal(second.Blockers, third.Blockers);
        Assert.Equal(first.IsReady, third.IsReady);
    }

    [Fact]
    public void NoBlockerIsEverTheDefaultEnumValue()
    {
        var result = PublicExposureReadinessEvaluator.Evaluate(new PublicExposureEvidence
        {
            SchemaVersion = PublicExposureEvidence.CurrentSchemaVersion
        });

        Assert.NotEmpty(result.Blockers);
        Assert.DoesNotContain(PublicExposureBlocker.None, result.Blockers);
    }

    private static PublicExposureEvidence WithBoolean(PublicExposureEvidence evidence, string property, bool? value)
    {
        // `with` cannot take a runtime property name, so the theory drives the record through its
        // init-only surface the only way it can: by re-projecting it.
        return property switch
        {
            nameof(PublicExposureEvidence.SetupWizardCompleted) => evidence with { SetupWizardCompleted = value },
            nameof(PublicExposureEvidence.EnabledAdministratorExists) => evidence with { EnabledAdministratorExists = value },
            nameof(PublicExposureEvidence.AdministratorPasswordSet) => evidence with { AdministratorPasswordSet = value },
            nameof(PublicExposureEvidence.AdministratorCredentialPolicySatisfied) => evidence with { AdministratorCredentialPolicySatisfied = value },
            nameof(PublicExposureEvidence.BackendTransportConstrained) => evidence with { BackendTransportConstrained = value },
            nameof(PublicExposureEvidence.ForwardedHeadersTrustProxyIdentity) => evidence with { ForwardedHeadersTrustProxyIdentity = value },
            nameof(PublicExposureEvidence.CertificateReady) => evidence with { CertificateReady = value },
            nameof(PublicExposureEvidence.ExternalReachabilityVerified) => evidence with { ExternalReachabilityVerified = value, IPv4ReachabilityVerified = evidence.IPv4ReachabilityVerified },
            nameof(PublicExposureEvidence.PublicAccessEnabled) => evidence with { PublicAccessEnabled = value },
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unmapped evidence property.")
        };
    }
}
