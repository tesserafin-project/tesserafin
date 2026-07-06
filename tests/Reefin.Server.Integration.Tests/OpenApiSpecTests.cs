using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Reefin.Model.IO;
using Xunit;

namespace Reefin.Server.Integration.Tests
{
    public sealed class OpenApiSpecTests : IClassFixture<ReefinApplicationFactory>
    {
        private readonly ReefinApplicationFactory _factory;
        private readonly ITestOutputHelper _outputHelper;

        public OpenApiSpecTests(ReefinApplicationFactory factory, ITestOutputHelper outputHelper)
        {
            _factory = factory;
            _outputHelper = outputHelper;
        }

        [Fact]
        public async Task GetSpec_ReturnsCorrectResponse()
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.GetAsync("/api-docs/openapi.json", TestContext.Current.CancellationToken);

            // Assert
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());

            // Write out for publishing
            string outputPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "openapi.json"));
            _outputHelper.WriteLine("Writing OpenAPI Spec JSON to '{0}'.", outputPath);
            await using var fs = AsyncFile.Create(outputPath);
            await response.Content.CopyToAsync(fs, TestContext.Current.CancellationToken);
        }
    }
}
