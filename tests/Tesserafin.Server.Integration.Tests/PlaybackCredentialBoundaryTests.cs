using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Tesserafin.Api.Models.PlaybackCredentialDtos;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Extensions.Json;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

/// <summary>
/// Request-level evidence for the #153-A0 boundary, against a real HTTP pipeline.
/// </summary>
/// <remarks>
/// This is the third and narrowest level of evidence. The primitives are proven in
/// <c>PlaybackCredentialServiceTests</c>, the route table in <c>MediaBoundaryStructuralTests</c>,
/// and what remains for a live pipeline is the question neither of those can answer: does a real
/// request carrying a real capability actually get refused by the real authorization stack on an
/// endpoint that is not media.
/// </remarks>
public sealed class PlaybackCredentialBoundaryTests : IClassFixture<TesserafinApplicationFactory>
{
    private readonly TesserafinApplicationFactory _factory;
    private static string? _accessToken;

    public PlaybackCredentialBoundaryTests(TesserafinApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        _accessToken ??= await AuthHelper.CompleteStartupAsync(client);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            AuthHelper.DummyAuthHeader + $", Token=\"{_accessToken}\"");
        return client;
    }

    private static async Task<PlaybackCapabilityDto> MintFontCapabilityAsync(HttpClient client)
    {
        // Fonts is the one scope that needs no item, so this mints without depending on a library
        // fixture — the boundary being tested is authorization, not media.
        using var response = await client.PostAsJsonAsync(
            "/Playback/Capabilities",
            new PlaybackCapabilityRequestDto
            {
                PlaySessionId = "integration-play-session",
                Scopes = new[] { PlaybackCapabilityScope.Fonts }
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PlaybackCapabilityDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(dto);
        Assert.NotEmpty(dto!.Value);
        return dto;
    }

    [Fact]
    public async Task Minting_requires_the_durable_token_in_a_header()
    {
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.PostAsJsonAsync(
            "/Playback/Capabilities",
            new PlaybackCapabilityRequestDto
            {
                PlaySessionId = "anonymous",
                Scopes = new[] { PlaybackCapabilityScope.Fonts }
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_capability_cannot_authenticate_a_general_api_endpoint()
    {
        // THE headline claim of A0. /Items is the endpoint the phase-0 inventory named: an ordinary
        // [Authorize] that reads the same query credential media does. A capability presented there
        // is not a weak credential — it is not a credential at all, because AuthorizationContext
        // reads only ApiKey and api_key and is never taught a third key.
        var client = await AuthenticatedClientAsync();
        var capability = await MintFontCapabilityAsync(client);

        using var anonymous = _factory.CreateClient();
        using var response = await anonymous.GetAsync($"/Items?playbackCapability={Uri.EscapeDataString(capability.Value)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_capability_cannot_authenticate_the_minting_endpoint_that_issued_it()
    {
        // "Never minted or renewed through a URL credential." If this ever passed, a capability
        // could mint its own successor and the fifteen-minute expiry would mean nothing.
        var client = await AuthenticatedClientAsync();
        var capability = await MintFontCapabilityAsync(client);

        using var anonymous = _factory.CreateClient();
        using var response = await anonymous.PostAsJsonAsync(
            $"/Playback/Capabilities?playbackCapability={Uri.EscapeDataString(capability.Value)}",
            new PlaybackCapabilityRequestDto
            {
                PlaySessionId = "escalation",
                Scopes = new[] { PlaybackCapabilityScope.Fonts }
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_websocket_ticket_cannot_authenticate_an_http_request()
    {
        var client = await AuthenticatedClientAsync();
        using var mint = await client.PostAsync(
            "/WebSocket/Tickets",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var ticket = await mint.Content.ReadFromJsonAsync<WebSocketTicketDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(ticket);

        using var anonymous = _factory.CreateClient();

        // Neither as its own key nor smuggled into the key the general API does read.
        using var asTicket = await anonymous.GetAsync($"/Items?webSocketTicket={Uri.EscapeDataString(ticket!.Value)}", TestContext.Current.CancellationToken);
        using var asApiKey = await anonymous.GetAsync($"/Items?ApiKey={Uri.EscapeDataString(ticket.Value)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, asTicket.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asApiKey.StatusCode);
    }

    [Fact]
    public async Task A_capability_smuggled_into_the_ApiKey_parameter_is_still_not_a_token()
    {
        // The obvious attempt: if a capability were accepted wherever the durable token is read,
        // every scope and item binding would be decoration.
        var client = await AuthenticatedClientAsync();
        var capability = await MintFontCapabilityAsync(client);

        using var anonymous = _factory.CreateClient();
        using var response = await anonymous.GetAsync($"/Items?ApiKey={Uri.EscapeDataString(capability.Value)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Renewal_before_the_window_is_refused_over_HTTP()
    {
        var client = await AuthenticatedClientAsync();
        var capability = await MintFontCapabilityAsync(client);

        using var response = await client.PostAsync(
            $"/Playback/Capabilities/{capability.CapabilityId}/Renew",
            content: null,
            TestContext.Current.CancellationToken);

        // 400, not 401: "too early" is the one refusal a correct client can act on, so it is the
        // one refusal that gets its own status.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Renewing_a_capability_that_does_not_exist_is_refused_without_saying_why()
    {
        var client = await AuthenticatedClientAsync();

        using var response = await client.PostAsync(
            $"/Playback/Capabilities/{Guid.NewGuid()}/Renew",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The framework's own ProblemDetails body is fine; what must NOT be there is which of the
        // refusals applied. Unknown, expired, revoked and not-yours have to look identical, or the
        // response becomes an oracle for which capabilities exist.
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var failure in Enum.GetNames<PlaybackCapabilityFailure>())
        {
            Assert.DoesNotContain(failure, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_minted_credential_is_never_cached()
    {
        var client = await AuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            "/Playback/Capabilities",
            new PlaybackCapabilityRequestDto
            {
                PlaySessionId = "cache-check",
                Scopes = new[] { PlaybackCapabilityScope.Fonts }
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_bound_scope_without_an_item_is_refused_rather_than_silently_unmatchable()
    {
        var client = await AuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            "/Playback/Capabilities",
            new PlaybackCapabilityRequestDto
            {
                PlaySessionId = "no-item",
                Scopes = new[] { PlaybackCapabilityScope.Media }
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
