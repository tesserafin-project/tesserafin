using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog.Events;
using Tesserafin.Api.Middleware;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Configuration;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="BaseUrlRedirectionMiddleware"/> logs two request-derived strings before it
    /// redirects: the incoming local path and the relative target it computed. Both are pinned here
    /// through the same real Kestrel listener the redirect suite uses, plus the middleware boundary
    /// directly, so neither the record count nor the redirect behaviour can drift.
    /// </summary>
    /// <remarks>
    /// Neither value can carry a raw <c>CR</c> or <c>LF</c> by the time it is logged:
    /// <c>localPath</c> is <c>PathString.ToString()</c>, which is <c>ToUriComponent()</c>, and
    /// <c>target</c> comes back out of <see cref="Uri.MakeRelativeUri(Uri)"/>. That is recorded as a
    /// measurement below rather than asserted as a reason to leave the statements alone: the
    /// boundary is what makes the guarantee independent of both.
    /// </remarks>
    public sealed class BaseUrlRedirectionMiddlewareLogTests
    {
        private const string ForgedTail = "%0D%0A%5B12:00:00.000%5D%20%5BERR%5D%20forged";

        public static TheoryData<string> HostileRequestTargets() => new()
        {
            "/library" + ForgedTail,
            "/%0d%0aSet-Cookie:%20injected=1",
            "/%250D%250A",
            "/..%2F..%2Fattacker.invalid",
        };

        [Theory]
        [MemberData(nameof(HostileRequestTargets))]
        public async Task RealPipeline_HostileRequestTarget_WritesExactlyOnePhysicalRecordPerLoggedValue(string requestTarget)
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await using var server = await StartAsync(probe, baseUrl: "/tesserafin");

            var response = await server.SendAsync("GET " + requestTarget + " HTTP/1.1");

            if (response.StatusCode is null)
            {
                // Kestrel refused the request line: the value never reaches the middleware.
                Assert.Equal(string.Empty, probe.Raw);
                return;
            }

            // Two statements run on the redirect path — the local path and the computed target —
            // and each must occupy exactly one physical record.
            Assert.Equal(2, probe.Lines().Length);
            Assert.Equal(2, probe.TextRecordCount());
            Assert.DoesNotContain('\r', probe.Raw);
            Assert.DoesNotContain("] [ERR]", probe.Raw, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RealPipeline_LiteralSeparatorInTheRequestTarget_NeverReachesTheMiddleware()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await using var server = await StartAsync(probe, baseUrl: "/tesserafin");

            await server.SendRawAsync(
                "GET /a\r\nX-Injected: 1\r\n HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");

            // Measured: a literal CR/LF cannot survive the request line, so the logging statements
            // are not remotely reachable with one. The boundary is applied regardless.
            Assert.DoesNotContain("X-Injected", probe.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Theory]
        [InlineData("/movies")]
        [InlineData("/a/b/c/d/e")]
        [InlineData("/movies?sort=name")]
        public async Task RealPipeline_OrdinaryRequestTarget_LogsTheSameTwoLinesItLoggedBefore(string requestTarget)
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await using var server = await StartAsync(probe, baseUrl: "/tesserafin");

            var response = await server.SendAsync("GET " + requestTarget + " HTTP/1.1");

            Assert.Equal(302, response.StatusCode);

            var lines = probe.Lines();
            Assert.Equal(2, lines.Length);
            Assert.Contains("Normalizing an URL at " + PathOf(requestTarget), lines[0], StringComparison.Ordinal);
            Assert.Contains("Redirecting to ", lines[1], StringComparison.Ordinal);
        }

        [Fact]
        public async Task RealPipeline_RequestInsideTheConfiguredBaseUrl_StillRedirectsNothingAndLogsNothing()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await using var server = await StartAsync(probe, baseUrl: "/tesserafin");

            var response = await server.SendAsync("GET /tesserafin/web/index.html HTTP/1.1");

            Assert.Equal(200, response.StatusCode);
            Assert.Null(response.Location);
            Assert.Equal(string.Empty, probe.Raw);
        }

        [Fact]
        public async Task RealPipeline_HealthProbe_KeepsItsOwnRedirectAndItsOwnRecord()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            await using var server = await StartAsync(probe, baseUrl: "/tesserafin");

            var response = await server.SendAsync("GET /health HTTP/1.1");

            Assert.Equal(302, response.StatusCode);
            Assert.Equal("/tesserafin/health", response.Location);
            var record = Assert.Single(probe.Lines());
            Assert.Contains("Redirecting /health check", record, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("/library\r\n[12:00:00.000] [ERR] forged")]
        [InlineData("/library\r")]
        [InlineData("/library\n")]
        public async Task Boundary_SeparatorInThePathString_WritesExactlyOnePhysicalRecordPerLoggedValue(string localPath)
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            var context = new DefaultHttpContext();
            context.Request.Path = new PathString(localPath);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("127.0.0.1", 8096);

            await InvokeAsync(probe, context, baseUrl: "/tesserafin");

            Assert.Equal(2, probe.Lines().Length);
            Assert.Equal(2, probe.TextRecordCount());
            Assert.DoesNotContain('\r', probe.Raw);
            Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        }

        [Fact]
        public async Task Boundary_OrdinaryPath_LeavesTheRedirectLocationUntouched()
        {
            using var probe = RealFormatterLogProbe.Text(LogEventLevel.Verbose);

            var context = new DefaultHttpContext();
            context.Request.Path = new PathString("/movies");
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("127.0.0.1", 8096);

            await InvokeAsync(probe, context, baseUrl: "/tesserafin");

            // This tranche changes logging arguments only: the emitted target is still the relative
            // reference the redirect suite pins.
            var location = context.Response.Headers.Location.ToString();
            Assert.Equal("tesserafin/web/", location);

            var lines = probe.Lines();
            Assert.Contains("Normalizing an URL at /movies", lines[0], StringComparison.Ordinal);
            Assert.Contains("Redirecting to tesserafin/web/", lines[1], StringComparison.Ordinal);
        }

        private static string PathOf(string requestTarget)
        {
            var query = requestTarget.IndexOf('?', StringComparison.Ordinal);
            return query < 0 ? requestTarget : requestTarget[..query];
        }

        private static async Task InvokeAsync(RealFormatterLogProbe probe, HttpContext context, string baseUrl)
        {
            var middleware = new BaseUrlRedirectionMiddleware(
                _ => Task.CompletedTask,
                probe.LoggerFor<BaseUrlRedirectionMiddleware>(),
                Configuration());

            await middleware.Invoke(context, ConfigurationManager(baseUrl));
        }

        private static Task<RawKestrelServer> StartAsync(RealFormatterLogProbe probe, string baseUrl)
            => RawKestrelServer.StartAsync(
                app =>
                {
                    app.UseMiddleware<BaseUrlRedirectionMiddleware>(
                        probe.LoggerFor<BaseUrlRedirectionMiddleware>(),
                        Configuration());
                    app.Run(context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        return Task.CompletedTask;
                    });
                },
                services => services.AddSingleton(ConfigurationManager(baseUrl)));

        private static IConfiguration Configuration()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DefaultRedirectPath"] = "web/" })
                .Build();

        private static IServerConfigurationManager ConfigurationManager(string baseUrl)
        {
            var manager = new Mock<IServerConfigurationManager>();
            manager
                .Setup(x => x.GetConfiguration(NetworkConfigurationStore.StoreKey))
                .Returns(new NetworkConfiguration { BaseUrl = baseUrl });
            return manager.Object;
        }
    }
}
