using System;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Moq;
using Tesserafin.Api.Middleware;
using Tesserafin.Common.Net;
using Tesserafin.Extensions;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="IPBasedAccessValidationMiddleware"/> already ran the request path through
    /// <see cref="HttpUtility.UrlEncode(string)"/> before logging it, and that encoder does
    /// neutralise <c>CR</c> and <c>LF</c>. These tests pin that fact first, then pin that adding the
    /// shared logging boundary after it changes nothing an operator can observe.
    /// </summary>
    /// <remarks>
    /// The boundary is added for two reasons that have nothing to do with the encoder being broken.
    /// It makes the guarantee independent of which characters a future encoder happens to escape,
    /// and it is the construct the repository-local CodeQL model recognises — the analyser cannot
    /// see that <c>UrlEncode</c> is sufficient here, and the alert is not dismissed as a false
    /// positive on the strength of an argument no tool can check.
    /// </remarks>
    public sealed class IpBasedAccessValidationMiddlewareLogTests
    {
        private const string HostilePath = "/library\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;

        [Fact]
        public void UrlEncode_HostilePath_AlreadyTurnsSeparatorsIntoOrdinaryText()
        {
            // Byte for byte: neither separator survives as itself.
            var encoded = HttpUtility.UrlEncode(HostilePath);

            Assert.NotNull(encoded);
            Assert.DoesNotContain('\r', encoded);
            Assert.DoesNotContain('\n', encoded);
            Assert.Contains("%0d%0a", encoded, StringComparison.Ordinal);
        }

        [Fact]
        public void PathString_HostilePath_IsAlreadyEscapedBeforeTheEncoderEvenSeesIt()
        {
            // The middleware does not hand UrlEncode the raw path: PathString.ToString() is
            // ToUriComponent(), which percent-encodes CR and LF first. Recorded so the tranche does
            // not claim UrlEncode is the only thing standing between a request and a forged record.
            var path = new PathString(HostilePath);

            var component = path.ToString();

            Assert.StartsWith("/library\r\n", path.Value!, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', component);
            Assert.DoesNotContain('\n', component);
            Assert.Contains("%0D%0A", component, StringComparison.Ordinal);
        }

        [Fact]
        public void ToSingleLogLine_AfterUrlEncode_ReturnsTheSameStringUnchanged()
        {
            var encoded = HttpUtility.UrlEncode(HostilePath);

            var flattened = encoded.ToSingleLogLine();

            // Not merely equal: the helper returns the same reference when there is nothing to
            // flatten, so the added call cannot have rewritten the encoder's output.
            Assert.Same(encoded, flattened);
        }

        [Theory]
        [InlineData(HostilePath)]
        [InlineData("/library\r")]
        [InlineData("/library\n")]
        [InlineData("/Items/0f1a")]
        public async Task Invoke_BlockedRequest_WritesExactlyOnePhysicalRecord(string requestPath)
        {
            using var probe = RealFormatterLogProbe.Text();

            await InvokeBlockedAsync(probe, requestPath);

            Assert.Single(probe.Lines());
            Assert.Equal(1, probe.TextRecordCount());
            Assert.DoesNotContain('\r', probe.Raw);
        }

        [Fact]
        public async Task Invoke_HostilePath_KeepsTheForgedPrefixInsideTheRealRecord()
        {
            using var probe = RealFormatterLogProbe.Text();

            await InvokeBlockedAsync(probe, HostilePath);

            var record = Assert.Single(probe.Lines());

            // The payload is still readable — this is not redaction — but it is percent-encoded
            // text inside the server's own record, not a record of its own. PathString escaped the
            // separators once and UrlEncode escaped that escape, hence %250D%250A.
            Assert.Contains("%250D%250A", record, StringComparison.Ordinal);
            Assert.Contains("administrator%2520account%2520deleted%2520by%2520mallory", record, StringComparison.Ordinal);
            Assert.DoesNotContain(RealFormatterLogProbe.ForgedRecordPrefix, record, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_OrdinaryPath_LogsTheSameLineItLoggedBefore()
        {
            using var probe = RealFormatterLogProbe.Text();

            await InvokeBlockedAsync(probe, "/Items/0f1a");

            var record = Assert.Single(probe.Lines());
            Assert.Contains("Blocking request to %2fItems%2f0f1a by 203.0.113.5", record, StringComparison.Ordinal);
            Assert.Contains("reason: \"RejectDueToIPBlocklist\"", record, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_AllowedRequest_LogsNothingAndCallsTheNextMiddleware()
        {
            using var probe = RealFormatterLogProbe.Text();
            var called = false;

            var middleware = new IPBasedAccessValidationMiddleware(
                _ =>
                {
                    called = true;
                    return Task.CompletedTask;
                },
                probe.LoggerFor<IPBasedAccessValidationMiddleware>());

            var networkManager = new Mock<INetworkManager>();
            networkManager
                .Setup(x => x.ShouldAllowServerAccess(It.IsAny<IPAddress>()))
                .Returns(RemoteAccessPolicyResult.Allow);

            await middleware.Invoke(RemoteContext(HostilePath), networkManager.Object);

            Assert.True(called);
            Assert.Equal(string.Empty, probe.Raw);
        }

        private static async Task InvokeBlockedAsync(RealFormatterLogProbe probe, string requestPath)
        {
            var middleware = new IPBasedAccessValidationMiddleware(
                _ => Task.FromException(new InvalidOperationException("The blocked path must not continue.")),
                probe.LoggerFor<IPBasedAccessValidationMiddleware>());

            var networkManager = new Mock<INetworkManager>();
            networkManager
                .Setup(x => x.ShouldAllowServerAccess(It.IsAny<IPAddress>()))
                .Returns(RemoteAccessPolicyResult.RejectDueToIPBlocklist);

            var context = RemoteContext(requestPath);

            await middleware.Invoke(context, networkManager.Object);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        }

        private static DefaultHttpContext RemoteContext(string requestPath)
        {
            var context = new DefaultHttpContext();
            context.Connection.LocalIpAddress = IPAddress.Parse("10.0.0.1");
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
            context.Request.Path = new PathString(requestPath);
            return context;
        }
    }
}
