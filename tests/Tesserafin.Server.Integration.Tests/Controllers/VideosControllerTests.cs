using System;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers;

public sealed class VideosControllerTests : IClassFixture<TesserafinApplicationFactory>
{
    private readonly TesserafinApplicationFactory _factory;
    private static string? _accessToken;

    public VideosControllerTests(TesserafinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DeleteAlternateSources_NonexistentItemId_NotFound()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.DeleteAsync($"Videos/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
