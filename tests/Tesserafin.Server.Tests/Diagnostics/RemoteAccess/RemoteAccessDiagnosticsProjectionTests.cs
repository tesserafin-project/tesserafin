using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Tesserafin.Extensions.Json;
using Tesserafin.Server.Api.RemoteAccess;
using Tesserafin.Server.Api.RemoteAccess.Models;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// The boundary between the R1-A engine and the published contract (R1-P, #248).
/// </summary>
/// <remarks>
/// These are contract tests, not implementation tests. The projector exists so that the internal
/// records are not the contract; the point of testing it is to make the ways it could silently
/// stop being a boundary — a value that maps to nothing, two values that collapse onto one, a
/// permanent unknown that gets dropped, an overall verdict that creeps in — fail loudly.
/// </remarks>
public sealed class RemoteAccessDiagnosticsProjectionTests
{
    /// <summary>Words that would assert a conclusion no code inside this host can reach.</summary>
    private static readonly string[] _verdictWords =
    {
        "Ready", "Healthy", "Reachable", "Working", "Available", "CanPublish", "Score", "Percent"
    };

    private static RemoteAccessDiagnosticSnapshot Snapshot(
        PublicationPolicyInput? input = null,
        DnsObservation? dns = null,
        IReadOnlyList<ClassifiedAddress>? addresses = null)
        => new(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            input ?? new PublicationPolicyInput(null, null, null),
            new BackendPostureObservation(true, BackendBindPosture.LoopbackOnly, false, 8096, 8920),
            new ProxyTrustObservation(Array.Empty<string>(), 0, false),
            addresses ?? Array.Empty<ClassifiedAddress>(),
            Array.Empty<PortListenerObservation>(),
            dns ?? new DnsObservation(null, DnsLookupOutcome.NotAttempted, Array.Empty<IPAddress>()));

    private static RemoteAccessDiagnosticsReportDto Project(RemoteAccessDiagnosticSnapshot snapshot)
        => RemoteAccessDiagnosticsProjector.ToWire(RemoteAccessDiagnosticEvaluator.Evaluate(snapshot));

    // ---------------------------------------------------------------- vocabulary lockstep

    [Fact]
    public void EveryInternalDiagnosticCodeHasAnIntentionalWireValue()
    {
        // The whole reason the projector spells out fifty arms instead of casting: a cast would
        // make every future code "work" and mean nothing. This walks the internal enum so that
        // adding a value without deciding how to publish it fails here rather than in a client.
        var unmapped = new List<string>();
        foreach (var code in Enum.GetValues<RemoteAccessDiagnosticCode>())
        {
            try
            {
                var wire = RemoteAccessDiagnosticsProjector.ToWireCode(code);
                Assert.Equal(code.ToString(), wire.ToString());
            }
            catch (InvalidOperationException)
            {
                unmapped.Add(code.ToString());
            }
        }

        Assert.True(unmapped.Count == 0, $"Diagnostic codes with no intentional wire disposition: {string.Join(", ", unmapped)}");
    }

    [Fact]
    public void TheTwoVocabulariesAreTheSameSize()
    {
        // Same-size plus same-names plus the per-value check above is what makes the mapping
        // exhaustive in BOTH directions: a wire value nobody can produce would show up here.
        Assert.Equal(
            Enum.GetValues<RemoteAccessDiagnosticCode>().Length,
            Enum.GetValues<RemoteAccessFindingCode>().Length);
        Assert.Equal(
            Enum.GetNames<RemoteAccessDiagnosticCode>().OrderBy(n => n, StringComparer.Ordinal),
            Enum.GetNames<RemoteAccessFindingCode>().OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void NoWireCodeIsReachableFromTwoInternalCodes()
    {
        // A collapse would silently merge two distinct diagnostics into one, and the report would
        // still look complete.
        var seen = new Dictionary<RemoteAccessFindingCode, RemoteAccessDiagnosticCode>();
        foreach (var code in Enum.GetValues<RemoteAccessDiagnosticCode>())
        {
            var wire = RemoteAccessDiagnosticsProjector.ToWireCode(code);
            Assert.False(
                seen.ContainsKey(wire),
                $"Wire code {wire} is produced by both {seen.GetValueOrDefault(wire)} and {code}.");
            seen[wire] = code;
        }
    }

    [Theory]
    [InlineData(typeof(DiagnosticConfidence), typeof(RemoteAccessFindingConfidence))]
    [InlineData(typeof(DiagnosticSeverity), typeof(RemoteAccessFindingSeverity))]
    [InlineData(typeof(BackendBindPosture), typeof(RemoteAccessBackendBindPosture))]
    [InlineData(typeof(ListenerObservationOutcome), typeof(RemoteAccessListenerOutcome))]
    [InlineData(typeof(DnsLookupOutcome), typeof(RemoteAccessDnsOutcome))]
    public void EveryInternalEnumHasAMatchingWireEnum(Type internalEnum, Type wireEnum)
    {
        Assert.Equal(
            Enum.GetNames(internalEnum).OrderBy(n => n, StringComparer.Ordinal),
            Enum.GetNames(wireEnum).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void TheCensusHasExactlyOneCounterPerAddressClass()
    {
        // There is deliberately NO wire enum mirroring AddressClass: the contract exposes a census
        // of counts, because the classes decide every topology finding and the addresses decide
        // none. That leaves a gap a name-parity test would have caught and this one closes — every
        // internal class must have somewhere to be counted, so adding one without extending the
        // census fails here instead of silently vanishing from the report.
        var counters = typeof(RemoteAccessLocalAddressCensusDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(counters);

        var missing = Enum.GetNames<AddressClass>()
            .Where(n => !string.Equals(n, nameof(AddressClass.None), StringComparison.Ordinal))
            .Where(n => !counters.Contains($"{n}Count"))
            .ToList();

        Assert.True(missing.Count == 0, $"Address classes with no census counter: {string.Join(", ", missing)}");
        // And no counter that counts nothing.
        Assert.Equal(Enum.GetNames<AddressClass>().Length - 1, counters.Count);
    }

    [Fact]
    public void NoneIsReservedAndIsNeverEmittedFromAValidReport()
    {
        // Reserved, not success. A report that emitted None would be saying nothing while looking
        // like it had said something.
        Assert.Equal(0, (int)RemoteAccessFindingCode.None);
        Assert.Equal(0, (int)RemoteAccessFindingConfidence.None);
        Assert.Equal(0, (int)RemoteAccessFindingSeverity.None);

        var report = Project(Snapshot());
        Assert.NotEmpty(report.Findings);
        Assert.DoesNotContain(report.Findings, f => f.Code == RemoteAccessFindingCode.None);
        Assert.DoesNotContain(report.Findings, f => f.Confidence == RemoteAccessFindingConfidence.None);
        Assert.DoesNotContain(report.Findings, f => f.Severity == RemoteAccessFindingSeverity.None);
    }

    // ---------------------------------------------------------------- projection fidelity

    [Fact]
    public void FindingsKeepTheEngineOrder()
    {
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(Snapshot());
        var wire = RemoteAccessDiagnosticsProjector.ToWire(report);

        Assert.Equal(
            report.Findings.Select(f => f.Code.ToString()).ToArray(),
            wire.Findings.Select(f => f.Code.ToString()).ToArray());
    }

    [Fact]
    public void AllFourPermanentUnknownsSurviveProjection()
    {
        // The four things nothing inside this host can know. An API layer that dropped them would
        // turn "we cannot see this" into a convenient silence.
        var codes = Project(Snapshot()).Findings.Select(f => f.Code).ToHashSet();

        Assert.Contains(RemoteAccessFindingCode.ExternalReachabilityUnverified, codes);
        Assert.Contains(RemoteAccessFindingCode.FirewallStateUnknown, codes);
        Assert.Contains(RemoteAccessFindingCode.RouterMappingUnknown, codes);
        Assert.Contains(RemoteAccessFindingCode.CertificateReadinessUnverified, codes);
    }

    [Theory]
    [InlineData(null, RemoteAccessPublicationPolicy.Unspecified)]
    [InlineData(false, RemoteAccessPublicationPolicy.DoNotPublish)]
    [InlineData(true, RemoteAccessPublicationPolicy.Publish)]
    public void ThePolicyIsEchoedAsTheServerUnderstoodIt(bool? internalValue, RemoteAccessPublicationPolicy expected)
    {
        var report = Project(Snapshot(new PublicationPolicyInput(null, internalValue, internalValue)));

        Assert.Equal(expected, report.Input.IPv4Policy);
        Assert.Equal(expected, report.Input.IPv6Policy);
    }

    [Theory]
    [InlineData(RemoteAccessPublicationPolicy.Unspecified, null)]
    [InlineData(RemoteAccessPublicationPolicy.DoNotPublish, false)]
    [InlineData(RemoteAccessPublicationPolicy.Publish, true)]
    public void TheRequestMapsOntoTheEngineInputExactly(RemoteAccessPublicationPolicy wire, bool? expected)
    {
        var input = RemoteAccessDiagnosticsProjector.ToInput(new RemoteAccessDiagnosticsRequestDto
        {
            Hostname = "example.test",
            IPv4Policy = wire,
            IPv6Policy = wire
        });

        Assert.Equal(expected, input.PublishIPv4);
        Assert.Equal(expected, input.PublishIPv6);
        Assert.Equal("example.test", input.ProposedHostname);
    }

    [Fact]
    public void AnUnknownPolicyValueFailsRatherThanBecomingSomethingPermissive()
    {
        // Unreachable through model binding, which requires the field — asserted anyway, because
        // the failure mode being prevented is "absent quietly became unspecified, or worse".
        var request = new RemoteAccessDiagnosticsRequestDto
        {
            IPv4Policy = (RemoteAccessPublicationPolicy)int.MaxValue,
            IPv6Policy = RemoteAccessPublicationPolicy.Unspecified
        };

        Assert.Throws<InvalidOperationException>(() => RemoteAccessDiagnosticsProjector.ToInput(request));
    }

    [Fact]
    public void ObservationsComeFromTheFrozenSnapshotRatherThanBeingRecollected()
    {
        // The projector is handed one report and must describe THAT instant. If it re-read
        // anything from the machine, two projections of the same report would disagree.
        var snapshot = Snapshot(addresses: new[]
        {
            new ClassifiedAddress(IPAddress.Loopback, AddressClass.Loopback),
            new ClassifiedAddress(IPAddress.Parse("192.168.1.4"), AddressClass.Private),
            new ClassifiedAddress(IPAddress.Parse("100.64.0.1"), AddressClass.SharedAddressSpace)
        });
        var report = RemoteAccessDiagnosticEvaluator.Evaluate(snapshot);

        var first = RemoteAccessDiagnosticsProjector.ToWire(report);
        var second = RemoteAccessDiagnosticsProjector.ToWire(report);

        Assert.Equal(snapshot.CollectedAt, first.CollectedAt);
        Assert.Equal(1, first.LocalAddresses.LoopbackCount);
        Assert.Equal(1, first.LocalAddresses.PrivateCount);
        Assert.Equal(1, first.LocalAddresses.SharedAddressSpaceCount);
        Assert.Equal(0, first.LocalAddresses.GloballyRoutableCount);
        Assert.Equal(
            JsonSerializer.Serialize(first, JsonDefaults.PascalCaseOptions),
            JsonSerializer.Serialize(second, JsonDefaults.PascalCaseOptions));
    }

    [Fact]
    public void TheReportCarriesTheEngineSchemaVersion()
    {
        Assert.Equal(RemoteAccessDiagnosticReport.CurrentSchemaVersion, Project(Snapshot()).SchemaVersion);
    }

    // ---------------------------------------------------------------- what must never appear

    [Fact]
    public void NoWireTypeCanExpressAnOverallVerdict()
    {
        // The affordance is the risk: a caller handed one boolean acts on the boolean and never
        // reads the findings. Checked over the whole wire namespace rather than one type, because
        // a verdict added to an observation would be just as fatal as one on the report.
        var offenders = new List<string>();
        foreach (var type in WireTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (_verdictWords.Any(w => property.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                    || (property.Name.Contains("Secure", StringComparison.OrdinalIgnoreCase)
                        && !property.Name.Contains("SecureBootstrap", StringComparison.Ordinal)))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"These wire members read as an overall verdict: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoWireTypeExposesHostIdentityBeyondWhatExplainsAFinding()
    {
        // A diagnostic report is a thing operators paste into issue trackers. None of these
        // identifies a machine or a household network, and none of them explains a finding.
        string[] forbidden =
        {
            "Mac", "PhysicalAddress", "InterfaceName", "Adapter", "Gateway", "DnsSuffix",
            "ProcessId", "MachineName", "UserName", "Domain"
        };

        var offenders = (from type in WireTypes()
                         from property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         where forbidden.Any(f => property.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                         select $"{type.Name}.{property.Name}").ToList();

        Assert.True(offenders.Count == 0, $"These wire members expose host identity: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoWireTypeExposesARawAddressList()
    {
        // The census and the DNS counts are deliberate: they decide every finding, and the
        // addresses themselves decide none. An IPAddress-shaped member would put the host's public
        // address into the report without explaining anything.
        var offenders = (from type in WireTypes()
                         from property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         where typeof(IPAddress).IsAssignableFrom(property.PropertyType)
                            || (property.PropertyType.IsGenericType
                                && property.PropertyType.GetGenericArguments().Any(a => typeof(IPAddress).IsAssignableFrom(a)))
                         select $"{type.Name}.{property.Name}").ToList();

        Assert.True(offenders.Count == 0, $"These wire members carry raw addresses: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheSerializedReportCarriesPascalCaseNamesAndNamedEnumValues()
    {
        // Repository convention is PascalCase (JsonDefaults.PascalCaseOptions sets no naming
        // policy), and enums travel as NAMES. Asserted rather than assumed: a client that received
        // `3` instead of `High` would have to hard-code CLR numbering to read the contract.
        var json = JsonSerializer.Serialize(Project(Snapshot()), JsonDefaults.PascalCaseOptions);

        Assert.Contains("\"SchemaVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"CollectedAt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"IPv4Policy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"IPv6Policy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Findings\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"findings\"", json, StringComparison.Ordinal);

        Assert.Contains("\"ExternalReachabilityUnverified\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Unspecified\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSerializedReportNamesNoInternalType()
    {
        var json = JsonSerializer.Serialize(Project(Snapshot()), JsonDefaults.PascalCaseOptions);

        foreach (var name in new[]
                 {
                     "RemoteAccessDiagnosticReport", "RemoteAccessDiagnosticSnapshot",
                     "PublicationPolicyInput", "Tesserafin.Server.Diagnostics.RemoteAccess"
                 })
        {
            Assert.DoesNotContain(name, json, StringComparison.Ordinal);
        }
    }

    private static IReadOnlyList<Type> WireTypes()
    {
        var types = typeof(RemoteAccessDiagnosticsReportDto).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(RemoteAccessDiagnosticsReportDto).Namespace && t.IsClass)
            .ToList();

        // A scanner that inspected nothing would report green.
        Assert.NotEmpty(types);
        return types;
    }
}
