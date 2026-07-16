using System;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Reefin.Server.Integration.Tests
{
    /// <summary>
    /// PR114: the self-contained admin diagnostics page is served as a plain static asset (the same
    /// mechanism already serving Swagger UI/ReDoc's <c>custom.css</c> under <c>wwwroot</c>) rather
    /// than through a controller - so, unlike every other admin surface in this suite, its own HTTP
    /// response is never gated by <c>Policies.RequiresElevation</c>. That is intentional and mirrors
    /// the Swagger UI precedent: the page shell (HTML/CSS/JS) is public, but every request the page's
    /// own JavaScript makes against <c>System/PlaybackDiagnostics/Sessions</c> still goes through
    /// <see cref="Reefin.Api.Controllers.PlaybackDiagnosticsSessionsController"/>, which remains
    /// <c>[Authorize(Policy = Policies.RequiresElevation)]</c> - see
    /// <c>Reefin.Api.Tests.Controllers.PlaybackDiagnosticsSessionsControllerTests.Controller_RequiresElevation</c>
    /// for that policy assertion, and <see cref="Sessions_WithoutToken_RequiresAuthorization"/> below
    /// for the end-to-end confirmation that the real data endpoint still rejects an anonymous caller.
    /// </summary>
    public sealed class DiagnosticsUiTests : IClassFixture<ReefinApplicationFactory>
    {
        private readonly ReefinApplicationFactory _factory;

        public DiagnosticsUiTests(ReefinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task DiagnosticsPage_ReturnsHtmlWithoutAuthentication()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/diagnostics-ui/index.html", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Playback Diagnostics", body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Sessions_WithoutToken_RequiresAuthorization()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/System/PlaybackDiagnostics/Sessions", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
