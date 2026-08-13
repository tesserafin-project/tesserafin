using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// Properties of this layer that are absences, and therefore cannot be observed by calling it.
/// </summary>
/// <remarks>
/// Two things are defended here. That nothing in the diagnostic layer opens a connection — which
/// is what separates a name lookup from a server-side request forgery primitive — and that no
/// type in it can express a summary verdict, because the affordance is the risk: a caller handed
/// one boolean will act on the boolean and never read the findings.
/// </remarks>
public sealed class RemoteAccessDiagnosticStructuralTests
{
    /// <summary>
    /// Anything that could open or use a socket, or reach the network by another route.
    /// </summary>
    /// <remarks>
    /// <c>Dns.GetHostAddressesAsync</c> is the one permitted outbound call and is matched
    /// separately, so the resolver can resolve without being allowed to connect.
    /// </remarks>
    private static readonly Regex _connectionApi = new(
        @"\b(new\s+(Socket|TcpClient|UdpClient|HttpClient|ClientWebSocket|SmtpClient)\b|\.(ConnectAsync|Connect|SendAsync|SendAndReceive|GetAsync|PostAsync|PutAsync|DeleteAsync|GetStreamAsync|GetStringAsync|OpenRead|DownloadString)\s*\()",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Words that would assert a conclusion this layer cannot reach.
    /// </summary>
    /// <remarks>
    /// Matched as substrings, so the list is deliberately short and specific. Broader words —
    /// "Ok", "Valid", "Success" — collide with ordinary vocabulary (<c>DnsLookupTimedOut</c>
    /// contains "ok"; <c>HostnameSyntacticallyInvalid</c> contains "valid") and a gate that cries
    /// wolf on those gets deleted rather than obeyed.
    /// </remarks>
    private static readonly string[] _verdictWords = { "Ready", "Healthy", "Reachable", "Working" };

    /// <summary>
    /// Names allowed to contain a verdict word because they name a thing, not a claim.
    /// </summary>
    /// <remarks>
    /// <c>SecureBootstrap</c> is the proper name of the mode R0-B introduced. It reports whether
    /// a specific, observable binding constraint is switched on; it asserts nothing about whether
    /// the server is secure, and renaming the feature to satisfy a substring check would trade a
    /// real name for a lint.
    /// </remarks>
    private static readonly string[] _permittedNames = { "SecureBootstrap" };

    private static bool ReadsAsAVerdict(string name)
    {
        foreach (var permitted in _permittedNames)
        {
            if (name.Contains(permitted, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return _verdictWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))
            || name.Contains("Secure", StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate Tesserafin.sln above '{AppContext.BaseDirectory}'. This gate inspects sources and cannot run without them.");
    }

    private static IReadOnlyList<string> LayerSourceFiles()
    {
        var root = Path.Combine(RepositoryRoot(), "Tesserafin.Server", "Diagnostics", "RemoteAccess");
        Assert.True(Directory.Exists(root), $"Expected the diagnostic layer at '{root}'.");

        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        return files;
    }

    private static string WithoutCommentLines(string source)
        => string.Join(
            '\n',
            source
                .Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();
                    return !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith('*');
                }));

    [Fact]
    public void NothingInTheLayerOpensAConnection()
    {
        // The no-probe gate. An address returned by DNS is an answer, not a destination; the
        // moment this layer could connect to one, an authenticated administrator would have a
        // request-forgery primitive pointed at the server's own network position.
        var offenders = new List<string>();

        foreach (var file in LayerSourceFiles())
        {
            var code = WithoutCommentLines(File.ReadAllText(file));
            if (_connectionApi.IsMatch(code))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These diagnostic sources reach the network beyond a name lookup: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheOnlyOutboundCallIsAHostnameLookup()
    {
        var resolver = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "Tesserafin.Server", "Diagnostics", "RemoteAccess", "SystemHostnameResolver.cs"));

        Assert.Contains("Dns.GetHostAddressesAsync", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", WithoutCommentLines(resolver), StringComparison.Ordinal);
        Assert.DoesNotContain("TcpClient", WithoutCommentLines(resolver), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingInTheLayerWritesConfiguration()
    {
        // Collection is a read. A configuration write reachable from a diagnostic would make the
        // act of asking a question change the answer.
        var offenders = new List<string>();

        foreach (var file in LayerSourceFiles())
        {
            var code = WithoutCommentLines(File.ReadAllText(file));
            if (code.Contains("SaveConfiguration", StringComparison.Ordinal)
                || code.Contains("UpdateSettings", StringComparison.Ordinal)
                || code.Contains("File.WriteAll", StringComparison.Ordinal)
                || code.Contains("File.Create", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These diagnostic sources write state: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NothingInTheLayerBindsOrListens()
    {
        var offenders = new List<string>();

        foreach (var file in LayerSourceFiles())
        {
            var code = WithoutCommentLines(File.ReadAllText(file));
            if (code.Contains(".Bind(", StringComparison.Ordinal) || code.Contains(".Listen(", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0, $"These diagnostic sources bind a socket: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoTypeInTheLayerCanExpressASummaryVerdict()
    {
        // A single roll-up is the affordance that lets a caller skip the findings and act on a
        // word, and every word available would be a claim this layer cannot support.
        var offenders = new List<string>();

        var types = typeof(RemoteAccessDiagnosticReport).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(RemoteAccessDiagnosticReport).Namespace)
            .ToList();

        Assert.NotEmpty(types);

        foreach (var type in types)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(bool) && property.PropertyType != typeof(bool?))
                {
                    continue;
                }

                if (ReadsAsAVerdict(property.Name))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These members read as an overall verdict: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheVocabularyContainsNoVerdictWords()
    {
        var offenders = Enum.GetNames<RemoteAccessDiagnosticCode>()
            .Concat(Enum.GetNames<DiagnosticConfidence>())
            .Concat(Enum.GetNames<ListenerObservationOutcome>())
            .Where(ReadsAsAVerdict)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These vocabulary entries claim more than this layer can know: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheDiagnosticCodesAreAStableContract()
    {
        // These names are what a future operator-facing surface renders and keys its translations
        // off. Renaming one is a contract change and has to be visible in review.
        var expected = new[]
        {
            "None",
            "SecureBootstrapActive", "SecureBootstrapInactive",
            "BackendLoopbackOnly", "BackendWildcardBound", "BackendLanBound",
            "BackendUnixSocketConfigured", "BackendExposureUnknown",
            "ListenerObservedOnPort80", "NoListenerObservedOnPort80",
            "ListenerObservedOnPort443", "NoListenerObservedOnPort443",
            "ListenerInspectionUnavailable", "PossibleExistingIngressOwner",
            "KnownProxiesAbsent", "KnownProxiesMalformed", "MultipleKnownProxiesConfigured",
            "ForwardedHeadersDisabled", "ForwardedHeadersEnabledConsistently",
            "ForwardedHeaderTrustInconsistent", "SameHostProxyLoopbackTrustTrapPossible",
            "HostnameNotProvided", "HostnameSyntacticallyInvalid",
            "DnsLookupSucceeded", "DnsNoAddressRecords", "DnsLookupTimedOut",
            "DnsLookupFailed", "DnsLookupCancelled",
            "DnsResultContainsIPv4", "DnsResultContainsIPv6",
            "DnsAddressMatchesLocalGlobalAddress", "DnsAddressMatchesNoLocalAddress",
            "PrivateAddressingObserved", "SharedAddressSpaceObserved",
            "DirectPublicAddressPossible", "NatOrUpstreamProxyPossiblyRequired",
            "CgNatSignalObserved", "CgNatNotDeterminable", "IpFamilyPolicyDisagreement",
            "ExternalReachabilityUnverified", "CertificateReadinessUnverified",
            "FirewallStateUnknown", "RouterMappingUnknown"
        };

        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.Ordinal),
            Enum.GetNames<RemoteAccessDiagnosticCode>().OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void TheConfidenceVocabularyIsExactlyTheFiveAgreedValues()
    {
        Assert.Equal(
            new[] { "Contradictory", "Derived", "None", "Observed", "Unknown", "Unverified" },
            Enum.GetNames<DiagnosticConfidence>().OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void NothingInTheLayerReadsTheClock()
    {
        // The collection instant is stamped once at the boundary and carried in the snapshot. A
        // classification rule that reached for the current time would make evaluation
        // non-deterministic for a fixed snapshot, and the determinism gate would stop meaning
        // anything.
        var offenders = new List<string>();

        foreach (var file in LayerSourceFiles())
        {
            if (string.Equals(Path.GetFileName(file), "RemoteAccessDiagnosticCollector.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var code = WithoutCommentLines(File.ReadAllText(file));
            if (code.Contains("DateTime.Now", StringComparison.Ordinal)
                || code.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                || code.Contains("DateTimeOffset.Now", StringComparison.Ordinal)
                || code.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0, $"These diagnostic sources read the clock: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NothingInTheLayerExposesHostIdentityBeyondAddresses()
    {
        // A diagnostic report is a thing operators paste into issues. MAC addresses, interface
        // names and DNS suffixes identify a machine and a household network, and none of them is
        // needed to answer any question this layer asks.
        var offenders = new List<string>();

        foreach (var file in LayerSourceFiles())
        {
            var code = WithoutCommentLines(File.ReadAllText(file));
            if (code.Contains("GetPhysicalAddress", StringComparison.Ordinal)
                || code.Contains("DnsSuffix", StringComparison.Ordinal)
                || code.Contains("GatewayAddresses", StringComparison.Ordinal)
                || code.Contains(".Description", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These diagnostic sources expose host identity beyond addresses: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheLayerDoesNotTouchTheR0BReadinessEvidence()
    {
        // R1 observes; it does not vote. Populating a PublicExposureEvidence field from here would
        // let a local observation stand in for evidence that only an external vantage point can
        // produce.
        var offenders = new List<string>();

        foreach (var file in LayerSourceFiles())
        {
            var code = WithoutCommentLines(File.ReadAllText(file));
            if (code.Contains("PublicExposureEvidence", StringComparison.Ordinal)
                || code.Contains("PublicExposureReadinessEvaluator", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These diagnostic sources reach into the R0-B readiness evidence: {string.Join(", ", offenders)}");
    }
}
