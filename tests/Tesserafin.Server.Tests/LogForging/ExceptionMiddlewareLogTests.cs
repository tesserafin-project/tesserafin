using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Moq;
using Tesserafin.Api.Middleware;
using Tesserafin.Controller.Configuration;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="ExceptionMiddleware"/> logs three request-derived strings: the exception message,
    /// the request method and the request path. These tests separate what a remote caller can
    /// actually put into each of them from what only an in-process caller can, and pin that the
    /// physical-record guarantee holds either way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reachability differs per argument and the difference is recorded rather than papered over:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>ex.Message</c> is reachable with <c>CR</c>/<c>LF</c> through the real pipeline — nothing
    /// stops an exception message from containing separators.
    /// </description></item>
    /// <item><description>
    /// <c>Request.Method</c> is not: Kestrel rejects a request line whose method token contains
    /// <c>CR</c> or <c>LF</c>, so the value never reaches dispatch. The middleware boundary is
    /// still exercised directly, because the guarantee must not depend on the parser.
    /// </description></item>
    /// <item><description>
    /// <c>Request.Path</c> is not: <c>PathString.ToString()</c> is <c>ToUriComponent()</c>, which
    /// percent-encodes the separators before the logger sees them.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed class ExceptionMiddlewareLogTests
    {
        private const string HostileTail = "\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;

        [Fact]
        public async Task RealPipeline_HostileExceptionMessage_WritesExactlyOnePhysicalRecord()
        {
            using var probe = RealFormatterLogProbe.Text();

            // IOException is one of the types the middleware logs without a stack trace, so the
            // message is a template argument rather than part of the exception block.
            await using var server = await StartAsync(
                probe,
                () => throw new IOException("disk offline" + HostileTail));

            var response = await server.SendAsync("GET /library HTTP/1.1");

            Assert.Equal(500, response.StatusCode);
            Assert.Single(probe.Lines());
            Assert.Equal(1, probe.TextRecordCount());
            Assert.Contains("\\r\\n", probe.Raw, StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by mallory", probe.Raw, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RealPipeline_OrdinaryException_LogsTheRequestUnchanged()
        {
            using var probe = RealFormatterLogProbe.Text();

            await using var server = await StartAsync(probe, () => throw new IOException("disk offline."));

            await server.SendAsync("GET /library HTTP/1.1");

            var record = Assert.Single(probe.Lines());
            Assert.Contains("Error processing request: disk offline. URL GET /library.", record, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("GE\rT /library HTTP/1.1")]
        [InlineData("GE\nT /library HTTP/1.1")]
        public async Task RealPipeline_SeparatorInTheMethodToken_NeverReachesTheMiddleware(string requestLine)
        {
            using var probe = RealFormatterLogProbe.Text();

            await using var server = await StartAsync(probe, () => throw new IOException("unreachable"));

            var response = await server.SendRawAsync(requestLine + "\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");

            // Measured, not assumed: the parser refuses the request line, so the application
            // pipeline — and therefore this logging statement — is never entered.
            Assert.NotEqual(500, response.StatusCode);
            Assert.Equal(string.Empty, probe.Raw);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Boundary_HostileMethod_WritesExactlyOnePhysicalRecord(bool ignoreStackTrace)
        {
            using var probe = RealFormatterLogProbe.Text();

            var context = new DefaultHttpContext();
            context.Request.Method = "GET" + HostileTail;
            context.Request.Path = new PathString("/library");

            await InvokeAsync(probe, context, ignoreStackTrace);

            // The parser would not have produced this method, but the boundary does not rely on
            // that: the value is flattened at the logger either way.
            Assert.Equal(1, probe.TextRecordCount());
            Assert.Contains("\\r\\n", probe.Raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\n[12:00:00.000]", probe.Raw, StringComparison.Ordinal);
            if (ignoreStackTrace)
            {
                Assert.Single(probe.Lines());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Boundary_HostilePath_WritesExactlyOnePhysicalRecord(bool ignoreStackTrace)
        {
            using var probe = RealFormatterLogProbe.Text();

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = new PathString("/library" + HostileTail);

            await InvokeAsync(probe, context, ignoreStackTrace);

            Assert.Equal(1, probe.TextRecordCount());

            // PathString already percent-encoded the separators, so there is nothing left for the
            // boundary to flatten. Recorded so the tranche does not overstate what it fixed.
            Assert.Contains("%0D%0A", probe.Raw, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Boundary_OrdinaryRequest_RendersTheSameMethodAndPathAsBefore(bool ignoreStackTrace)
        {
            using var probe = RealFormatterLogProbe.Text();

            var context = new DefaultHttpContext();
            context.Request.Method = "DELETE";
            context.Request.Path = new PathString("/Items/0f1a");

            await InvokeAsync(probe, context, ignoreStackTrace);

            Assert.Contains("URL DELETE /Items/0f1a.", probe.Raw, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task JsonFormatter_OrdinaryRequest_KeepsUrlAsAPlainStringProperty(bool ignoreStackTrace)
        {
            using var probe = RealFormatterLogProbe.Json();

            var context = new DefaultHttpContext();
            context.Request.Method = "DELETE";
            context.Request.Path = new PathString("/Items/0f1a");

            await InvokeAsync(probe, context, ignoreStackTrace);

            // The second statement now passes Request.Path.ToString() where it used to pass the
            // PathString itself. The container's formatter is where a change of captured type would
            // show up, so it is asserted there: Url stays a plain string, not an object.
            using var document = JsonDocument.Parse(Assert.Single(probe.Lines()));
            var url = document.RootElement.GetProperty("Url");
            Assert.Equal(JsonValueKind.String, url.ValueKind);
            Assert.Equal("/Items/0f1a", url.GetString());
            Assert.Equal("DELETE", document.RootElement.GetProperty("Method").GetString());
        }

        [Fact]
        public async Task JsonFormatter_HostileMethod_StaysOneRecordAndOneObject()
        {
            using var probe = RealFormatterLogProbe.Json();

            var context = new DefaultHttpContext();
            context.Request.Method = "GET" + HostileTail;
            context.Request.Path = new PathString("/library");

            await InvokeAsync(probe, context, ignoreStackTrace: true);

            using var document = JsonDocument.Parse(Assert.Single(probe.Lines()));
            var method = document.RootElement.GetProperty("Method").GetString();
            Assert.NotNull(method);
            Assert.DoesNotContain('\n', method);
            Assert.DoesNotContain('\r', method);
        }

        [Fact]
        public async Task Boundary_HostileValues_LeaveTheResponseUntouched()
        {
            using var probe = RealFormatterLogProbe.Text();

            var context = new DefaultHttpContext();
            context.Request.Method = "GET" + HostileTail;
            context.Request.Path = new PathString("/library");
            context.Response.Body = new MemoryStream();

            await InvokeAsync(probe, context, ignoreStackTrace: true);

            // This tranche changes logging arguments only.
            Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
            Assert.Equal("text/plain", context.Response.ContentType);
        }

        private static async Task InvokeAsync(
            RealFormatterLogProbe probe,
            DefaultHttpContext context,
            bool ignoreStackTrace)
        {
            context.Response.Body = context.Response.Body == Stream.Null ? new MemoryStream() : context.Response.Body;

            Exception failure = ignoreStackTrace
                ? new IOException("disk offline")
                : new InvalidOperationException("unexpected state");

            var middleware = new ExceptionMiddleware(
                _ => throw failure,
                probe.LoggerFor<ExceptionMiddleware>(),
                new Mock<IServerConfigurationManager>().Object,
                ProductionEnvironment());

            await middleware.Invoke(context);
        }

        private static Task<RawKestrelServer> StartAsync(RealFormatterLogProbe probe, Action throwing)
            => RawKestrelServer.StartAsync(
                app =>
                {
                    app.UseMiddleware<ExceptionMiddleware>(
                        probe.LoggerFor<ExceptionMiddleware>(),
                        new Mock<IServerConfigurationManager>().Object,
                        ProductionEnvironment());
                    app.Run(_ =>
                    {
                        throwing();
                        return Task.CompletedTask;
                    });
                },
                configuration: new Dictionary<string, string?>());

        private static IWebHostEnvironment ProductionEnvironment()
        {
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(x => x.EnvironmentName).Returns("Production");
            return environment.Object;
        }
    }
}
