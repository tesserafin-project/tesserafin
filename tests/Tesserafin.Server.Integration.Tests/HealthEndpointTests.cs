using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Common;
using Tesserafin.Server.HealthChecks;
using Xunit;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// The <c>/health</c> contract from #91 / [A5].
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #91 asks for the negative case to be "verified by a test that stops the DB". Tesserafin
    /// has no separately stoppable database: persistence is an embedded SQLite database opened
    /// in-process. So the intent is preserved exactly — <c>/health</c> must answer non-2xx when a real
    /// database probe fails — and the evidence is produced at the only seam that exists: the
    /// <see cref="IDatabaseHealthProbe"/> registration is replaced through the ordinary DI container.
    /// No external database process is stopped, and nothing here claims otherwise.
    /// </para>
    /// <para>
    /// The healthy case is NOT faked: <see cref="HealthProbeMode.Real"/> delegates to the production
    /// <see cref="DatabaseHealthProbe"/>, which executes a real <c>SELECT 1</c> against the real
    /// SQLite database this test host created.
    /// </para>
    /// </remarks>
    public sealed class HealthEndpointTests : IClassFixture<HealthApplicationFactory>
    {
        private readonly HealthApplicationFactory _factory;

        public HealthEndpointTests(HealthApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Health_RealDatabase_Returns200WithHealthyContract()
        {
            _factory.Probe.Mode = HealthProbeMode.Real;
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(HealthResponseWriter.StatusHealthy, document.RootElement.GetProperty("status").GetString());
            Assert.Equal(HealthResponseWriter.StatusHealthy, document.RootElement.GetProperty(DatabaseHealthCheck.Name).GetString());
        }

        [Fact]
        public async Task Health_ReportsTheExactApplicationVersion()
        {
            _factory.Probe.Mode = HealthProbeMode.Real;
            var client = _factory.CreateClient();

            var body = await client.GetStringAsync("/health", TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(body);
            var expected = _factory.Services.GetRequiredService<IApplicationHost>().ApplicationVersionString;
            Assert.Equal(expected, document.RootElement.GetProperty("version").GetString());
        }

        [Fact]
        public async Task Health_FailingDatabaseProbe_Returns503WithTheSameSchema()
        {
            _factory.Probe.Mode = HealthProbeMode.Fail;
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(body);

            // Same three fields, same names, same order-independent shape as the healthy case.
            Assert.Equal(
                new[] { DatabaseHealthCheck.Name, "status", "version" },
                document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(HealthResponseWriter.StatusUnhealthy, document.RootElement.GetProperty("status").GetString());
            Assert.Equal(HealthResponseWriter.StatusUnhealthy, document.RootElement.GetProperty(DatabaseHealthCheck.Name).GetString());
        }

        [Fact]
        public async Task Health_FailingDatabaseProbe_LeaksNoDiagnosticDetail()
        {
            _factory.Probe.Mode = HealthProbeMode.Fail;
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // The response writer serialises three fixed fields and nothing else. Guard the usual
            // leak vectors explicitly rather than trusting that convention holds.
            foreach (var forbidden in new[] { "Exception", "StackTrace", "at Tesserafin", "Data Source", "sqlite", ".db", "/config", "Description", "duration" })
            {
                Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Health_ProbeThatNeverAnswers_IsBoundedAndReturns503()
        {
            _factory.Probe.Mode = HealthProbeMode.Hang;
            var client = _factory.CreateClient();

            var stopwatch = Stopwatch.StartNew();
            var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(
                stopwatch.Elapsed < DatabaseHealthCheck.ProbeTimeout + TimeSpan.FromSeconds(20),
                $"/health took {stopwatch.Elapsed} — the probe bound was not applied.");

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(HealthResponseWriter.StatusUnhealthy, document.RootElement.GetProperty(DatabaseHealthCheck.Name).GetString());
        }
    }
}
