using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.PlaybackCredentialDtos;
using Tesserafin.Api.Models.StartupDtos;
using Tesserafin.Controller.Net;
using Tesserafin.Extensions.Json;
using Tesserafin.Server.Core.Session;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// One booted server for the #153-A0-R2 WebSocket upgrade matrix: real
/// <c>WebSocketHandlerMiddleware</c>, real <c>WebSocketManager</c>, real upgrade handshake.
/// </summary>
/// <remarks>
/// WHY NOT THE PRIMITIVE TESTS. <c>PlaybackCredentialServiceTests</c> proves
/// <c>ConsumeWebSocketTicket</c> in isolation, which is a statement about a dictionary. It cannot
/// say whether the upgrade path calls it, whether it calls it BEFORE accepting the socket, whether
/// a refused ticket falls back to the durable token, or what identity the accepted connection ends
/// up carrying. Those are the properties the contract actually promises, and every one of them
/// lives in <c>WebSocketManager.AuthenticateUpgrade</c> rather than in the store.
///
/// HOW A REFUSAL IS OBSERVED. Not by the exception type: <c>ConnectAsync</c> throws
/// <see cref="InvalidOperationException"/> both when the server refuses before accepting and when
/// it accepts and then drops the connection, so the exception alone proves nothing about ordering.
/// The discriminator is <c>SessionWebSocketListener</c>'s watchlist — a connection reaches it only
/// through <c>ProcessWebSocketConnectedAsync</c>, which runs only after
/// <c>AcceptWebSocketAsync</c> has returned. A refusal that never accepts leaves the watchlist
/// untouched, and that is what every negative case here asserts.
/// </remarks>
public sealed class WebSocketUpgradeFixture : IAsyncLifetime
{
    /// <summary>The device the fixture's own session uses.</summary>
    public const string PrimaryDeviceId = "r2-primary-device";

    /// <summary>A second device, for tickets that belong to somebody else.</summary>
    public const string OtherDeviceId = "r2-other-device";

    private string _userName = string.Empty;
    private string _password = string.Empty;

    /// <summary>Gets the booted server.</summary>
    public WebSocketUpgradeApplicationFactory Factory { get; private set; } = null!;

    /// <summary>Gets the primary session's durable token.</summary>
    public string DurableToken { get; private set; } = string.Empty;

    /// <summary>Gets the fixture user's id.</summary>
    public Guid UserId { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Factory = new WebSocketUpgradeApplicationFactory();

        using (var setup = Factory.CreateClient())
        {
            var startupUser = await setup
                .GetFromJsonAsync<StartupUserDto>("/Startup/User", JsonDefaults.Options)
                .ConfigureAwait(false);

            _userName = startupUser!.Name ?? string.Empty;
            _password = startupUser.Password ?? string.Empty;

            using var complete = await setup.PostAsync("/Startup/Complete", new ByteArrayContent([])).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        }

        DurableToken = await AuthenticateAsync(PrimaryDeviceId).ConfigureAwait(false);

        using var me = ClientFor(PrimaryDeviceId, DurableToken);
        using var document = JsonDocument.Parse(await me.GetStringAsync("/Users/Me").ConfigureAwait(false));
        UserId = Guid.Parse(document.RootElement.GetProperty("Id").GetString()!);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Authenticates the fixture's user on one device.
    /// </summary>
    /// <param name="deviceId">The device to authenticate as.</param>
    /// <returns>That session's durable token.</returns>
    public async Task<string> AuthenticateAsync(string deviceId)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, MediaBoundaryFixture.AuthorizationHeader(deviceId, null));
        request.Content = JsonContent.Create(new { Username = _userName, Pw = _password }, options: JsonDefaults.Options);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("AccessToken").GetString()!;
    }

    /// <summary>
    /// Creates an HTTP client presenting one device's durable token in the header.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="token">That device's token.</param>
    /// <returns>An authenticated client.</returns>
    public HttpClient ClientFor(string deviceId, string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            MediaBoundaryFixture.AuthorizationHeader(deviceId, token));
        return client;
    }

    /// <summary>
    /// Mints a WebSocket ticket over real HTTP, durable token in a header.
    /// </summary>
    /// <param name="deviceId">The device to mint for.</param>
    /// <param name="token">That device's durable token.</param>
    /// <returns>The minted ticket.</returns>
    public async Task<WebSocketTicketDto> MintTicketAsync(string deviceId, string token)
    {
        using var client = ClientFor(deviceId, token);
        using var response = await client.PostAsync("/WebSocket/Tickets", content: null).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content
            .ReadFromJsonAsync<WebSocketTicketDto>(JsonDefaults.Options)
            .ConfigureAwait(false);

        Assert.NotNull(dto);
        Assert.NotEmpty(dto!.Value);
        return dto;
    }

    /// <summary>
    /// Mints a ticket for the fixture's primary device.
    /// </summary>
    /// <returns>The minted ticket.</returns>
    public Task<WebSocketTicketDto> MintTicketAsync() => MintTicketAsync(PrimaryDeviceId, DurableToken);

    /// <summary>
    /// Opens the real socket presenting ONLY a ticket in the query string — no header, no token.
    /// </summary>
    /// <param name="ticket">The ticket value.</param>
    /// <param name="durableToken">An optional durable token to also present, for no-fallback cases.</param>
    /// <param name="deviceId">The device the optional header claims.</param>
    /// <returns>The connected socket.</returns>
    public Task<WebSocket> ConnectWithTicketAsync(string ticket, string? durableToken = null, string deviceId = PrimaryDeviceId)
        => ConnectAsync($"webSocketTicket={Uri.EscapeDataString(ticket)}", durableToken, deviceId);

    /// <summary>
    /// Opens the real socket the legacy way, durable token in the header.
    /// </summary>
    /// <param name="durableToken">The durable token.</param>
    /// <param name="deviceId">The device the header claims.</param>
    /// <returns>The connected socket.</returns>
    public Task<WebSocket> ConnectWithDurableTokenAsync(string durableToken, string deviceId = PrimaryDeviceId)
        => ConnectAsync(query: null, durableToken, deviceId);

    /// <summary>
    /// Opens the real socket with an arbitrary query string.
    /// </summary>
    /// <param name="query">The query string, without the leading '?'.</param>
    /// <param name="durableToken">An optional durable token for the header.</param>
    /// <param name="deviceId">The device the optional header claims.</param>
    /// <returns>The connected socket.</returns>
    public async Task<WebSocket> ConnectAsync(string? query, string? durableToken = null, string deviceId = PrimaryDeviceId)
    {
        var client = Factory.Server.CreateWebSocketClient();
        if (durableToken is not null)
        {
            client.ConfigureRequest = request =>
                request.Headers.Authorization = MediaBoundaryFixture.AuthorizationHeader(deviceId, durableToken);
        }

        var uri = new UriBuilder(Factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "websocket",
            Query = query ?? string.Empty
        }.Uri;

        return await client.ConnectAsync(uri, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the connections <c>SessionWebSocketListener</c> is watching. A connection reaches this
    /// list only after <c>AcceptWebSocketAsync</c> returned, so it is the instrument that separates
    /// "refused before accepting" from "accepted then dropped".
    /// </summary>
    /// <returns>A snapshot of the watchlist.</returns>
    public IReadOnlyList<IWebSocketConnection> Watchlist()
    {
        var listener = Factory.Services.GetRequiredService<IEnumerable<IWebSocketListener>>()
            .OfType<SessionWebSocketListener>()
            .Single();

        var field = typeof(SessionWebSocketListener)
            .GetField("_webSockets", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var watchlist = (IEnumerable<IWebSocketConnection>)field!.GetValue(listener)!;

        while (true)
        {
            try
            {
                return watchlist.ToArray();
            }
            catch (InvalidOperationException)
            {
                // The keep-alive watchdog mutated the set mid-enumeration; retry.
            }
        }
    }

    /// <summary>
    /// Waits until the watchlist reaches the expected size, or the timeout expires.
    /// </summary>
    /// <param name="expected">The size to wait for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The final snapshot.</returns>
    public async Task<IReadOnlyList<IWebSocketConnection>> WaitForWatchlistAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var snapshot = Watchlist();
            if (snapshot.Count >= expected || DateTime.UtcNow >= deadline)
            {
                return snapshot;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes a socket and waits for the listener to forget it, so the next case starts from a
    /// known watchlist.
    /// </summary>
    /// <param name="socket">The socket to close.</param>
    /// <returns>A task.</returns>
    public async Task CloseAndDrainAsync(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test over", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // Already gone.
        }

        socket.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Watchlist().Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends one bounded message and reads one back, so an accepted socket is proven to carry real
    /// traffic rather than merely to have completed a handshake.
    /// </summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="messageType">The message type to send.</param>
    /// <returns>The first message received, as text.</returns>
    public static async Task<string> ExchangeAsync(WebSocket socket, string messageType)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var payload = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{{\"MessageType\":\"{messageType}\"}}"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

        var buffer = new byte[8192];
        var received = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }
}
