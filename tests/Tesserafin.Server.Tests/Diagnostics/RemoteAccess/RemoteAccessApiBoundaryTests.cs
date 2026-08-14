using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// The permanent boundary around the R1-A engine, now that R1-P has given it an HTTP surface.
/// </summary>
/// <remarks>
/// THESE REPLACE TWO TEMPORARY GATES, THEY DO NOT DELETE THEM.
/// <c>NothingOutsideTheLayerReachesTheEngine</c> and
/// <c>TheCanonicalContractNamesNothingFromTheLayer</c> existed to forbid exactly this transition
/// while R1-A shipped alone: nothing could reach the engine, and the contract could not name it.
/// Both of those are now false by design, so keeping them would have meant deleting or weakening a
/// gate — the thing that quietly removes a protection under cover of a feature.
///
/// What replaces them is narrower and permanent. The engine is still unreachable from anywhere
/// EXCEPT an explicit three-file allowlist, and the contract still may not name the internal
/// namespace or the internal report and snapshot types — only the dedicated wire models. The
/// allowlist is spelled out by path rather than by directory pattern, so a fourth file reaching
/// the engine fails here even if it lives beside the three that may.
///
/// <c>NothingInTheLayerBindsToHttp</c> is untouched and still applies: the API layer lives outside
/// the engine directory precisely so that the engine keeps its own permanent no-HTTP gate.
/// </remarks>
public sealed class RemoteAccessApiBoundaryTests
{
    /// <summary>The only files permitted to name the engine's types. Explicit, by path.</summary>
    private static readonly string[] _approvedCallers =
    {
        Path.Combine("Tesserafin.Server", "Api", "RemoteAccess", "RemoteAccessDiagnosticsController.cs"),
        Path.Combine("Tesserafin.Server", "Api", "RemoteAccess", "RemoteAccessDiagnosticsProjector.cs"),
        Path.Combine("Tesserafin.Server", "Extensions", "ApiServiceCollectionExtensions.cs")
    };

    /// <summary>The engine's own type names, as a caller or a contract would spell them.</summary>
    private static readonly string[] _engineTypeNames =
    {
        "RemoteAccessDiagnosticCollector", "RemoteAccessDiagnosticEvaluator",
        "RemoteAccessDiagnosticReport", "RemoteAccessDiagnosticSnapshot",
        "RemoteAccessDiagnosticCode", "RemoteAccessFinding",
        "DiagnosticConfidence", "DiagnosticSeverity", "PublicationPolicyInput",
        "BackendPostureObservation", "BackendBindPosture", "ProxyTrustObservation",
        "PortListenerObservation", "ListenerObservationOutcome", "ClassifiedAddress",
        "AddressClassifier", "AddressClass", "HostnameInput", "DnsObservation",
        "DnsLookupOutcome", "ServerNetworkPostureSource", "SystemLocalAddressSource",
        "SystemTcpListenerSource", "SystemHostnameResolver", "ILocalAddressSource",
        "ITcpListenerSource", "IHostnameResolver", "INetworkPostureSource"
    };

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

    private static IReadOnlyList<string> ServerSourceFiles()
    {
        var root = RepositoryRoot();
        var layer = Path.Combine(root, "Tesserafin.Server", "Diagnostics", "RemoteAccess");

        var files = new[] { "Tesserafin.Server", "Tesserafin.Api", "Tesserafin.Server.Core" }
            .Select(project => Path.Combine(root, project))
            .Where(Directory.Exists)
            .SelectMany(project => Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.StartsWith(layer, StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // A gate that silently inspected nothing would report green.
        Assert.NotEmpty(files);
        return files;
    }

    private static string WithoutCommentLines(string source)
        => string.Join(
            '\n',
            source.Split('\n').Where(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith('*');
            }));

    private static bool IsApproved(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return _approvedCallers.Any(a => string.Equals(relative, a, StringComparison.Ordinal));
    }

    [Fact]
    public void TheAllowlistIsRealAndEveryEntryExists()
    {
        // An allowlist naming files that do not exist would permit nothing and prove nothing,
        // and would keep passing after a rename that moved the real caller out from under it.
        var root = RepositoryRoot();
        Assert.NotEmpty(_approvedCallers);
        foreach (var approved in _approvedCallers)
        {
            Assert.True(File.Exists(Path.Combine(root, approved)), $"Allowlisted file '{approved}' does not exist.");
        }
    }

    [Fact]
    public void OnlyTheApprovedCallersReachTheEngine()
    {
        // Replaces NothingOutsideTheLayerReachesTheEngine. The engine is still unreachable from
        // the entire server surface, with exactly three named exceptions.
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in ServerSourceFiles())
        {
            if (IsApproved(file, root))
            {
                continue;
            }

            var code = WithoutCommentLines(File.ReadAllText(file));
            foreach (var name in _engineTypeNames)
            {
                if (Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\b"))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)} ({name})");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These files reach the diagnostic engine but are not on the allowlist: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheApprovedCallersActuallyDoReachTheEngine()
    {
        // The converse, so the allowlist cannot rot into three entries that permit nothing. If a
        // caller stops touching the engine it should leave the allowlist, not sit there widening
        // the exception for free.
        var root = RepositoryRoot();
        foreach (var approved in _approvedCallers)
        {
            var code = WithoutCommentLines(File.ReadAllText(Path.Combine(root, approved)));
            Assert.True(
                _engineTypeNames.Any(n => Regex.IsMatch(code, $@"\b{Regex.Escape(n)}\b")),
                $"Allowlisted file '{approved}' no longer references the engine and should be removed from the allowlist.");
        }
    }

    [Fact]
    public void NoBackgroundOrHostedServiceReachesTheEngine()
    {
        // Collection happens because an administrator asked. A hosted service, scheduled task or
        // timer would make the server diagnose itself on its own schedule, which is a different
        // feature with different consent.
        var root = RepositoryRoot();
        var selfStarting = new Regex(
            @"(IHostedService|BackgroundService|IStartupFilter|IServerEntryPoint|IScheduledTask|PeriodicTimer|new\s+Timer\b)",
            RegexOptions.CultureInvariant);

        var offenders = (from file in ServerSourceFiles()
                         let code = WithoutCommentLines(File.ReadAllText(file))
                         where selfStarting.IsMatch(code)
                               && _engineTypeNames.Any(n => Regex.IsMatch(code, $@"\b{Regex.Escape(n)}\b"))
                         select Path.GetRelativePath(root, file)).ToList();

        Assert.True(offenders.Count == 0, $"These self-starting sources reach the engine: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ThereIsExactlyOneEvaluatorOneCollectorAndOneCodeCatalogue()
    {
        // A second copy of any of these is how a vocabulary drifts: one of them gets a new code,
        // the other does not, and the two disagree about what the server can say.
        var root = RepositoryRoot();
        foreach (var (name, file) in new[]
                 {
                     ("evaluator", "RemoteAccessDiagnosticEvaluator.cs"),
                     ("collector", "RemoteAccessDiagnosticCollector.cs"),
                     ("code catalogue", "RemoteAccessDiagnosticCode.cs")
                 })
        {
            var matches = Directory
                .GetFiles(root, file, SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList();

            Assert.True(matches.Count == 1, $"Expected exactly one {name}, found {matches.Count}: {string.Join(", ", matches)}");
        }
    }

    [Fact]
    public void TheControllerReturnsOnlyDedicatedWireTypes()
    {
        var root = RepositoryRoot();
        var controller = File.ReadAllText(Path.Combine(root, _approvedCallers[0]));
        var code = WithoutCommentLines(controller);

        // The action's declared return type is the serialization surface. If the internal report
        // appeared there, the contract would be the implementation record.
        Assert.Contains("ActionResult<RemoteAccessDiagnosticsReportDto>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ActionResult<RemoteAccessDiagnosticReport>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ActionResult<RemoteAccessDiagnosticSnapshot>", code, StringComparison.Ordinal);

        // The controller may name the engine only to call it and to project the result.
        Assert.Contains("RemoteAccessDiagnosticsProjector.ToWire", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NoApiFileWritesConfigurationOrTouchesTheR0BEvidence()
    {
        // R1-P observes. A configuration write reachable from a diagnostic would make asking the
        // question change the answer, and writing R0-B publication evidence would let a local
        // observation stand in for evidence only an external vantage point can produce.
        var root = RepositoryRoot();
        var apiDirectory = Path.Combine(root, "Tesserafin.Server", "Api", "RemoteAccess");
        var files = Directory.GetFiles(apiDirectory, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var code = WithoutCommentLines(File.ReadAllText(file));
            foreach (var forbidden in new[]
                     {
                         "SaveConfiguration", "UpdateSettings", "File.WriteAll", "File.Create",
                         "PublicExposureEvidence", "PublicExposureReadinessEvaluator"
                     })
            {
                if (code.Contains(forbidden, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)} ({forbidden})");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"These API sources mutate state or reach R0-B evidence: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoApiFileManagesNetworkOrCertificateState()
    {
        // The endpoint reports. Anything that could reconfigure a proxy, request a certificate or
        // open a port would make a diagnostic into an action.
        var root = RepositoryRoot();
        var files = Directory.GetFiles(
            Path.Combine(root, "Tesserafin.Server", "Api", "RemoteAccess"), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        // Deliberately specific. A bare "Firewall" would match the FirewallStateUnknown
        // diagnostic CODE NAME, which is the opposite of firewall management: it is the server
        // saying it cannot see the firewall.
        var mutation = new Regex(
            @"(Caddy|AcmeClient|LetsEncrypt|CertificateRequest|X509Certificate2\s*\(|UPnP|PortMapping|advfirewall|iptables|\.Bind\(|\.Listen\()",
            RegexOptions.CultureInvariant);

        var offenders = (from file in files
                         let code = WithoutCommentLines(File.ReadAllText(file))
                         let match = mutation.Match(code)
                         where match.Success
                         select $"{Path.GetFileName(file)} ({match.Value})").ToList();

        Assert.True(offenders.Count == 0, $"These API sources manage network or certificate state: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheCanonicalContractNamesTheWireModelsAndNotTheImplementation()
    {
        // Replaces TheCanonicalContractNamesNothingFromTheLayer. The contract must now name the
        // endpoint — that is the point of R1-P — but it must name the DEDICATED WIRE TYPES.
        //
        // Compared as exact schema keys rather than as substrings: `RemoteAccessFindingDto` legally
        // contains the internal name `RemoteAccessFinding`, and a substring test would either fail
        // on a correct contract or force the wire types into contorted names to satisfy a lint.
        var root = RepositoryRoot();
        var contractPath = Path.Combine(root, "openapi", "openapi.json");
        Assert.True(File.Exists(contractPath), $"Expected the canonical contract at '{contractPath}'.");

        var text = File.ReadAllText(contractPath);
        Assert.NotEmpty(text);

        using var document = System.Text.Json.JsonDocument.Parse(text);
        var schemas = document.RootElement
            .GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(schemas);

        // The internal namespace must never appear anywhere in the document, in any form.
        Assert.DoesNotContain("Tesserafin.Server.Diagnostics.RemoteAccess", text, StringComparison.Ordinal);

        foreach (var internalName in new[]
                 {
                     "RemoteAccessDiagnosticReport", "RemoteAccessDiagnosticSnapshot",
                     "RemoteAccessDiagnosticCode", "RemoteAccessFinding", "PublicationPolicyInput",
                     "BackendPostureObservation", "ProxyTrustObservation", "ClassifiedAddress",
                     "PortListenerObservation", "DnsObservation", "DiagnosticConfidence", "DiagnosticSeverity"
                 })
        {
            Assert.False(
                schemas.Contains(internalName),
                $"The canonical contract publishes the internal type '{internalName}' as a schema.");
        }

        // And the wire models must actually be there, so this cannot pass by the endpoint simply
        // being absent.
        foreach (var wireName in new[]
                 {
                     "RemoteAccessDiagnosticsRequestDto", "RemoteAccessDiagnosticsReportDto",
                     "RemoteAccessFindingDto", "RemoteAccessFindingCode", "RemoteAccessPublicationPolicy"
                 })
        {
            Assert.True(schemas.Contains(wireName), $"The canonical contract is missing the wire model '{wireName}'.");
        }
    }

    [Fact]
    public void TheCanonicalContractExposesExactlyOneDiagnosticsOperationAndNoHostnameInTheUrl()
    {
        var root = RepositoryRoot();
        var text = File.ReadAllText(Path.Combine(root, "openapi", "openapi.json"));
        using var document = System.Text.Json.JsonDocument.Parse(text);

        var paths = document.RootElement.GetProperty("paths").EnumerateObject().ToList();
        Assert.NotEmpty(paths);

        // `/Startup/RemoteAccess` predates R1-P and is a configuration endpoint, so the filter is
        // the diagnostics route exactly rather than anything mentioning remote access.
        var diagnostics = paths.Where(p => p.Name.Contains("RemoteAccess/Diagnostics", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(diagnostics.Count == 1, $"Expected exactly one remote-access diagnostics path, found {diagnostics.Count}.");
        Assert.Equal("/System/RemoteAccess/Diagnostics", diagnostics[0].Name);

        var methods = diagnostics[0].Value.EnumerateObject().Select(m => m.Name).ToList();
        Assert.Equal(new[] { "post" }, methods);

        // No hostname in the URL, by construction: no path template segment and no parameter of
        // any kind on the operation. A hostname in a URL is written to logs nobody can un-write.
        Assert.DoesNotContain("hostname", diagnostics[0].Name, StringComparison.OrdinalIgnoreCase);
        var operation = diagnostics[0].Value.GetProperty("post");
        if (operation.TryGetProperty("parameters", out var parameters))
        {
            var names = parameters.EnumerateArray()
                .Select(p => p.GetProperty("name").GetString() ?? string.Empty).ToList();
            Assert.DoesNotContain(names, n => n.Contains("hostname", StringComparison.OrdinalIgnoreCase));
            // Inherited global api_key handling is out of R1-P's scope, but this operation must
            // not declare or advertise a query credential of its own.
            Assert.DoesNotContain(names, n => string.Equals(n, "api_key", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheCanonicalContractContainsNoVerdictFieldOnTheDiagnosticsModels()
    {
        var root = RepositoryRoot();
        var text = File.ReadAllText(Path.Combine(root, "openapi", "openapi.json"));
        using var document = System.Text.Json.JsonDocument.Parse(text);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var offenders = new List<string>();

        foreach (var schema in schemas.EnumerateObject()
                     .Where(s => s.Name.StartsWith("RemoteAccess", StringComparison.Ordinal)))
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                foreach (var word in new[] { "Ready", "Healthy", "Reachable", "Working", "Available", "CanPublish", "Score", "Percent" })
                {
                    if (property.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add($"{schema.Name}.{property.Name}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, $"The contract publishes overall-verdict fields: {string.Join(", ", offenders)}");
    }
}
