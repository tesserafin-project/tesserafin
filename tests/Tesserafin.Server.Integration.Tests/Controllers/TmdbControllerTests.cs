using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers
{
    /// <summary>
    /// Pins what <c>tmdb/ClientConfiguration</c> answers when no TheMovieDb API key is configured —
    /// the state every fresh install starts in now that Tesserafin ships no built-in key. The TMDb
    /// plugin configuration page consumes this endpoint on load, and it is the only page from which
    /// an operator can supply a key, so its unconfigured answer is part of the contract rather than
    /// an implementation detail.
    /// </summary>
    public sealed class TmdbControllerTests : IClassFixture<TesserafinApplicationFactory>
    {
        private readonly TesserafinApplicationFactory _factory;
        private static string? _accessToken;

        public TmdbControllerTests(TesserafinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task TmdbClientConfiguration_WithoutConfiguredApiKey_AnswersWithoutServerError()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

            using var response = await client.GetAsync("/tmdb/ClientConfiguration", TestContext.Current.CancellationToken);

            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // 200 with a JSON null, NOT 204 No Content: the server's own JSON output formatters are
            // inserted ahead of HttpNoContentOutputFormatter, so a null result is still serialised.
            // The page's success handler reads that null and falls back to the stored image sizes; a
            // 204 with an empty body would instead reject its JSON parse and leave the page spinning,
            // so this status is load-bearing and pinned here rather than left to the default.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("null", body);
        }
    }
}
