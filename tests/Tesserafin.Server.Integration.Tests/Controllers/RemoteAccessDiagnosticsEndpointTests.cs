using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Extensions.Json;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers;

/// <summary>
/// The administrator diagnostics endpoint, over the real HTTP pipeline (R1-P, #248).
/// </summary>
/// <remarks>
/// Everything here goes through <see cref="RemoteAccessDiagnosticsApplicationFactory"/>, which
/// boots the production application and replaces only the four environment-facing diagnostic
/// sources and time. Authentication, authorization, routing, controller discovery, JSON options,
/// the projector and the collector's lifetime are all the real ones — calling the controller
/// directly and labelling that an HTTP proof would test nothing that ships.
/// </remarks>
public sealed class RemoteAccessDiagnosticsEndpointTests : IClassFixture<RemoteAccessDiagnosticsApplicationFactory>
{
    private const string Route = "/System/RemoteAccess/Diagnostics";

    private readonly RemoteAccessDiagnosticsApplicationFactory _factory;

    public RemoteAccessDiagnosticsEndpointTests(RemoteAccessDiagnosticsApplicationFactory factory)
    {
        _factory = factory;
    }

    private static HttpContent Body(string? hostname, string ipv4 = "Unspecified", string ipv6 = "Unspecified")
    {
        var hostnameJson = hostname is null ? "null" : $"\"{hostname}\"";
        return new StringContent(
            $$"""{"Hostname": {{hostnameJson}}, "IPv4Policy": "{{ipv4}}", "IPv6Policy": "{{ipv6}}"}""",
            Encoding.UTF8,
            "application/json");
    }

    private async Task<HttpClient> ElevatedAdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await _factory.AdminTokenAsync(client));
        return client;
    }

    // ------------------------------------------------------------------ authorization

    [Fact]
    public async Task AnonymousCallerIsRejected()
    {
        var client = _factory.CreateClient();

        using var response = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnElevatedAdministratorReceivesAReport()
    {
        var client = await ElevatedAdminAsync();

        using var response = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheOperationDeclaresNoApiKeyQueryParameterAndNoHostnameParameter()
    {
        // Inherited global support for `api_key` in a query string is out of R1-P's scope and is
        // NOT denied here — but this operation must not declare, advertise or add one of its own,
        // and the hostname must never be reachable through the URL.
        using var scope = _factory.Services.CreateScope();
        var descriptions = scope.ServiceProvider
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .Where(d => string.Equals("System/RemoteAccess/Diagnostics", d.RelativePath, StringComparison.Ordinal))
            .ToList();

        Assert.Single(descriptions);
        Assert.Equal("POST", descriptions[0].HttpMethod);

        var parameterNames = descriptions[0].ParameterDescriptions.Select(p => p.Name).ToList();
        Assert.DoesNotContain(parameterNames, n => string.Equals(n, "api_key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            descriptions[0].ParameterDescriptions.Where(p => p.Source.Id is "Query" or "Path"),
            p => p.Name.Contains("hostname", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExactlyOneDiagnosticsControllerIsDiscoveredThroughTheApplicationPart()
    {
        // The controller lives in Tesserafin.Server and is only reachable because that assembly is
        // registered as an application part after ApplicationParts.Clear(). If the registration
        // were dropped, this would find nothing and every HTTP test above would 404 instead.
        var descriptions = _factory.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .Where(d => d.RelativePath?.Contains("RemoteAccess/Diagnostics", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.Single(descriptions);
    }

    // ------------------------------------------------------------------ route

    [Fact]
    public async Task GetOnTheSameRouteIsNotTheEndpoint()
    {
        var client = await ElevatedAdminAsync();

        using var response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AHostnameInTheQueryStringIsNotUsedAsTheRequestHostname()
    {
        // The hostname travels in the body and only in the body. A query value must be inert.
        var client = await ElevatedAdminAsync();
        var before = _factory.Resolver.CallCount;

        using var response = await client.PostAsync(
            Route + "?hostname=query-only.example", Body(null), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("HostnameNotProvided", json, StringComparison.Ordinal);
        Assert.Equal(before, _factory.Resolver.CallCount);
        Assert.DoesNotContain("query-only.example", _factory.Resolver.Requested);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{"IPv6Policy": "Unspecified"}""")]
    [InlineData("""{"IPv4Policy": "Unspecified"}""")]
    [InlineData("""{"IPv4Policy": "Maybe", "IPv6Policy": "Unspecified"}""")]
    public async Task AMalformedOrIncompleteBodyIsAClientError(string body)
    {
        // A missing family policy is a client error and never a default: there is deliberately no
        // omission that could be read as permission to publish.
        var client = await ElevatedAdminAsync();

        using var response = await client.PostAsync(
            Route, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheResponseIsJsonAndIsNeverStored()
    {
        var client = await ElevatedAdminAsync();

        using var response = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", string.Join(",", response.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ request semantics

    [Theory]
    [InlineData("Unspecified", "IpFamilyPolicyUnresolved")]
    [InlineData("DoNotPublish", null)]
    [InlineData("Publish", null)]
    public async Task EachNamedPolicyValueBinds(string policy, string? expectedCode)
    {
        var client = await ElevatedAdminAsync();

        using var response = await client.PostAsync(
            Route, Body(null, policy, policy), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"\"{policy}\"", json, StringComparison.Ordinal);
        if (expectedCode is not null)
        {
            Assert.Contains(expectedCode, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnAbsentHostnameIsAnsweredRatherThanRejected()
    {
        var client = await ElevatedAdminAsync();
        var before = _factory.Resolver.CallCount;

        using var response = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("HostnameNotProvided", json, StringComparison.Ordinal);
        Assert.Equal(before, _factory.Resolver.CallCount);
    }

    [Fact]
    public async Task AnInvalidHostnameIsDiagnosticEvidenceAndNeverReachesTheResolver()
    {
        // The core of the hostname contract: "what you typed cannot be a hostname" is the answer
        // the operator came for, not a 400 — and the resolver must never see it.
        var client = await ElevatedAdminAsync();
        var before = _factory.Resolver.CallCount;

        using var response = await client.PostAsync(
            Route, Body("not a hostname/at all"), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("HostnameSyntacticallyInvalid", json, StringComparison.Ordinal);
        Assert.Equal(before, _factory.Resolver.CallCount);
        Assert.DoesNotContain(_factory.Resolver.Requested, r => r.Contains("not a hostname", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AValidHostnameReachesTheResolverExactlyOnce()
    {
        var client = await ElevatedAdminAsync();
        var before = _factory.Resolver.CallCount;

        using var response = await client.PostAsync(
            Route, Body("valid-once.example"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before + 1, _factory.Resolver.CallCount);
        Assert.Contains("valid-once.example", _factory.Resolver.Requested);
    }

    // ------------------------------------------------------------------ the emitted document

    [Fact]
    public async Task TheEmittedReportIsPascalCaseWithNamedValuesAndNoVerdict()
    {
        var client = await ElevatedAdminAsync();

        using var response = await client.PostAsync(
            Route, Body(null, "Publish", "DoNotPublish"), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Repository convention: PascalCase, because JsonDefaults.PascalCaseOptions sets no naming
        // policy. Asserted on the wire rather than inferred from the options object.
        Assert.True(root.TryGetProperty("SchemaVersion", out _));
        Assert.True(root.TryGetProperty("CollectedAt", out _));
        Assert.True(root.TryGetProperty("Findings", out var findings));
        Assert.False(root.TryGetProperty("schemaVersion", out _));

        Assert.Equal("Publish", root.GetProperty("Input").GetProperty("IPv4Policy").GetString());
        Assert.Equal("DoNotPublish", root.GetProperty("Input").GetProperty("IPv6Policy").GetString());

        // Every finding is a named triple, never a CLR number.
        foreach (var finding in findings.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, finding.GetProperty("Code").ValueKind);
            Assert.Equal(JsonValueKind.String, finding.GetProperty("Confidence").ValueKind);
            Assert.Equal(JsonValueKind.String, finding.GetProperty("Severity").ValueKind);
        }

        // The four permanent unknowns survive the trip to the wire.
        var codes = findings.EnumerateArray().Select(f => f.GetProperty("Code").GetString()).ToList();
        Assert.Contains("ExternalReachabilityUnverified", codes);
        Assert.Contains("FirewallStateUnknown", codes);
        Assert.Contains("RouterMappingUnknown", codes);
        Assert.Contains("CertificateReadinessUnverified", codes);

        // No verdict, no score, no internal CLR metadata.
        foreach (var forbidden in new[]
                 {
                     "IsReady", "IsSecure", "Healthy", "Reachable", "CanPublish", "Score",
                     "$type", "Tesserafin.Server.Diagnostics.RemoteAccess",
                     "RemoteAccessDiagnosticReport", "RemoteAccessDiagnosticSnapshot"
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FindingsKeepTheEngineOrderOverHttp()
    {
        var client = await ElevatedAdminAsync();

        using var response = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var codes = document.RootElement.GetProperty("Findings")
            .EnumerateArray().Select(f => f.GetProperty("Code").GetString()!).ToList();

        Assert.NotEmpty(codes);

        // The engine's own order, recomputed here from the same fake observations. Sorting or
        // grouping in the projection would show up as a mismatch.
        var snapshot = new RemoteAccessDiagnosticSnapshot(
            DateTimeOffset.UnixEpoch,
            new PublicationPolicyInput(null, null, null),
            _factory.Posture.Backend,
            _factory.Posture.Proxy,
            AddressClassifier.ClassifySet(_factory.Addresses.GetUnicastAddresses()),
            _factory.Listeners.Observe(new[] { 80, 443 }),
            new DnsObservation(null, DnsLookupOutcome.NotAttempted, Array.Empty<IPAddress>()));
        var expected = RemoteAccessDiagnosticEvaluator.Evaluate(snapshot)
            .Findings.Select(f => f.Code.ToString()).ToList();

        Assert.Equal(expected, codes);
    }

    [Fact]
    public async Task TwoRequestsProduceTwoDistinctReportsAndNothingIsCached()
    {
        var client = await ElevatedAdminAsync();

        using var first = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);
        using var second = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);

        var a = JsonDocument.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var b = JsonDocument.Parse(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The fake clock advances on every read, so an identical timestamp would mean a report was
        // reused rather than collected.
        Assert.NotEqual(
            a.RootElement.GetProperty("CollectedAt").GetString(),
            b.RootElement.GetProperty("CollectedAt").GetString());
    }

    // ------------------------------------------------------------------ logging

    [Fact]
    public async Task TheSubmittedHostnameNeverReachesTheLogs()
    {
        // A sentinel that passes hostname validation, so it travels the whole path: binding,
        // normalisation, the resolver, the snapshot and the response.
        const string Sentinel = "r1p-log-sentinel-8f3a2c.example";
        var client = await ElevatedAdminAsync();
        _factory.Logs.Clear();

        using var response = await client.PostAsync(Route, Body(Sentinel), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Proof the sentinel really did travel the full path — otherwise "absent from the logs"
        // would be trivially true.
        Assert.Contains(Sentinel, _factory.Resolver.Requested);

        var captured = _factory.Logs.Captured.ToList();
        Assert.NotEmpty(captured);

        // Messages, templates, structured state, scopes and exceptions are all captured, because a
        // hostname leaked as a structured value would never appear in a rendered message — and
        // structured values are exactly what telemetry pipelines ship.
        var leaks = captured.Where(c => c.Contains("r1p-log-sentinel", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(leaks.Count == 0, $"The submitted hostname reached the logs: {string.Join(" | ", leaks.Take(3))}");
    }

    [Fact]
    public async Task NeitherTheRequestBodyNorTheResponseBodyIsLogged()
    {
        var client = await ElevatedAdminAsync();
        _factory.Logs.Clear();

        using var response = await client.PostAsync(
            Route, Body(null, "Publish", "Publish"), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var captured = _factory.Logs.Captured.ToList();
        Assert.NotEmpty(captured);

        // A distinctive fragment of the response body, and the request body verbatim.
        Assert.DoesNotContain(captured, c => c.Contains("ExternalReachabilityUnverified", StringComparison.Ordinal));
        Assert.DoesNotContain(captured, c => c.Contains("\"IPv4Policy\": \"Publish\"", StringComparison.Ordinal));
        Assert.True(json.Length > 0);
    }

    // ------------------------------------------------------------------ lifetime and cancellation

    [Fact]
    public void TheCollectorIsOneProcessWideInstance()
    {
        // The descriptor alone would not prove this; two independent scopes resolving the same
        // reference does. The invariant is a semaphore field on that instance, so one instance is
        // exactly what makes it process-wide.
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<RemoteAccessDiagnosticCollector>(),
            second.ServiceProvider.GetRequiredService<RemoteAccessDiagnosticCollector>());
    }

    [Fact]
    public async Task NoCollectionHappensUntilAnAuthorizedRequestArrives()
    {
        // The host has been up for the whole fixture. If anything collected on startup — a hosted
        // service, a warm-up, a scheduled task — the address source would already have been read.
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            using var client = factory.CreateClient();
            Assert.Equal(0, factory.Addresses.MaxObservedConcurrency);

            using var anonymous = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            // Still nothing: an unauthorized request must not reach the collector either.
            Assert.Equal(0, factory.Addresses.MaxObservedConcurrency);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }
}
