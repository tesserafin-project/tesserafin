using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Middleware
{
    public sealed class RobotsRedirectionMiddlewareTests : IClassFixture<TesserafinApplicationFactory>
    {
        private readonly TesserafinApplicationFactory _factory;

        public RobotsRedirectionMiddlewareTests(TesserafinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task RobotsDotTxtRedirects()
        {
            var client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions()
                {
                    AllowAutoRedirect = false
                });

            var response = await client.GetAsync("robots.txt", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("web/robots.txt", response.Headers.Location?.ToString());
        }
    }
}
