using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Server.HealthChecks;
using Xunit;

namespace Tesserafin.Server.Tests
{
    /// <summary>
    /// The <c>/health</c> body contract from #91 / [A5], asserted at the writer so every branch —
    /// including the ones that are awkward to reach over HTTP — is covered.
    /// </summary>
    public class HealthResponseWriterTests
    {
        [Fact]
        public async Task Healthy_AfterCoreStartup_Is200AndHealthy()
        {
            var (context, body) = await WriteAsync(HealthStatus.Healthy, coreStartupHasCompleted: true);

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal("application/json", context.Response.ContentType);
            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal(HealthResponseWriter.StatusHealthy, body.GetProperty("status").GetString());
            Assert.Equal(HealthResponseWriter.StatusHealthy, body.GetProperty(DatabaseHealthCheck.Name).GetString());
        }

        [Fact]
        public async Task Healthy_BeforeCoreStartup_Is503AndStarting()
        {
            // The regression this pins: the real pipeline serves requests before the host has
            // finished core startup. Answering 200 there is a false ready signal even though the
            // database itself answers.
            var (context, body) = await WriteAsync(HealthStatus.Healthy, coreStartupHasCompleted: false);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
            Assert.Equal("application/json", context.Response.ContentType);
            Assert.Equal(HealthResponseWriter.StatusStarting, body.GetProperty("status").GetString());
            Assert.Equal(HealthResponseWriter.StatusHealthy, body.GetProperty(DatabaseHealthCheck.Name).GetString());
        }

        [Fact]
        public async Task UnhealthyDatabase_Is503AndUnhealthy()
        {
            var (context, body) = await WriteAsync(HealthStatus.Unhealthy, coreStartupHasCompleted: true);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
            Assert.Equal(HealthResponseWriter.StatusUnhealthy, body.GetProperty("status").GetString());
            Assert.Equal(HealthResponseWriter.StatusUnhealthy, body.GetProperty(DatabaseHealthCheck.Name).GetString());
        }

        [Fact]
        public async Task NoApplicationHost_UsesTheReportAsIs()
        {
            // The startup server has no IServerApplicationHost; its own check reports Degraded and
            // there is no database entry yet, so the shape must still hold with `unknown`.
            var (context, body) = await WriteAsync(HealthStatus.Degraded, coreStartupHasCompleted: null, includeDatabaseEntry: false);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
            Assert.Equal(HealthResponseWriter.StatusStarting, body.GetProperty("status").GetString());
            Assert.Equal(HealthResponseWriter.DatabaseUnknown, body.GetProperty(DatabaseHealthCheck.Name).GetString());
        }

        [Fact]
        public async Task Body_CarriesExactlyThreeFieldsAndNoDiagnostics()
        {
            var (_, body) = await WriteAsync(
                HealthStatus.Unhealthy,
                coreStartupHasCompleted: true,
                includeDatabaseEntry: true,
                description: "Data Source=/config/data/tesserafin.db",
                exception: new InvalidOperationException("connection string /config/data/tesserafin.db is broken"));

            Assert.Equal(
                new[] { DatabaseHealthCheck.Name, "status", "version" },
                body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());

            var raw = body.GetRawText();
            foreach (var forbidden in new[] { "Data Source", "tesserafin.db", "InvalidOperationException", "duration", "description" })
            {
                Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Version_FallsBackToTheAssemblyVersionWithoutAnApplicationHost()
        {
            var (_, body) = await WriteAsync(HealthStatus.Degraded, coreStartupHasCompleted: null, includeDatabaseEntry: false);

            Assert.Equal(HealthResponseWriter.FallbackVersion, body.GetProperty("version").GetString());
            Assert.Matches(@"^\d+\.\d+\.\d+$", body.GetProperty("version").GetString()!);
        }

        private static async Task<(HttpContext Context, JsonElement Body)> WriteAsync(
            HealthStatus status,
            bool? coreStartupHasCompleted,
            bool includeDatabaseEntry = true,
            string? description = null,
            Exception? exception = null)
        {
            var services = new ServiceCollection();
            if (coreStartupHasCompleted is not null)
            {
                var host = new Mock<IServerApplicationHost>();
                host.SetupGet(h => h.CoreStartupHasCompleted).Returns(coreStartupHasCompleted.Value);
                host.SetupGet(h => h.ApplicationVersionString).Returns("1.0.0");
                services.AddSingleton(host.Object);
                services.AddSingleton<Tesserafin.Common.IApplicationHost>(host.Object);
            }

            var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            context.Response.Body = new MemoryStream();

            var entries = new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal);
            if (includeDatabaseEntry)
            {
                entries[DatabaseHealthCheck.Name] = new HealthReportEntry(
                    status,
                    description,
                    TimeSpan.FromMilliseconds(3),
                    exception,
                    data: null);
            }

            var report = new HealthReport(entries, status, TimeSpan.FromMilliseconds(4));
            await HealthResponseWriter.WriteAsync(context, report);

            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
            return (context, document.RootElement.Clone());
        }
    }
}
