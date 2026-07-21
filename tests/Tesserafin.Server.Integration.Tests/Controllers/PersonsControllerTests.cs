using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers;

public class PersonsControllerTests : IClassFixture<TesserafinApplicationFactory>
{
    private readonly TesserafinApplicationFactory _factory;
    private static string? _accessToken;

    public PersonsControllerTests(TesserafinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetPerson_DoesntExist_NotFound()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        using var response = await client.GetAsync($"Persons/DoesntExist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
