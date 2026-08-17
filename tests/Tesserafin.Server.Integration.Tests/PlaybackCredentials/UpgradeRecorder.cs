using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Tesserafin.Controller.Net;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// A test-only <see cref="IWebSocketListener"/> that records every connection the server actually
/// accepted, together with the principal the upgrade ran under (#153-A0-R3).
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AND THE WATCHLIST DOES NOT SUFFICE. R2 discriminated "refused before accepting"
/// from "accepted then dropped" using <c>SessionWebSocketListener</c>'s watchlist. That instrument
/// has a blind spot R3's cases fall into: the watchlist is populated inside
/// <c>KeepAliveWebSocket</c>, which runs AFTER <c>RequestHelpers.GetSession</c>, so a socket that is
/// accepted and then dies resolving its session leaves the watchlist untouched and looks exactly
/// like a refusal. Every hostile control R3 names produces precisely that shape, so a control graded
/// on the watchlist alone would be inert.
///
/// <c>WebSocketManager</c> starts every listener's <c>ProcessWebSocketConnectedAsync</c> before
/// awaiting any of them, so this recorder observes an acceptance even when
/// <c>SessionWebSocketListener</c> throws on the same connection. That makes
/// <see cref="Accepted"/> an unconditional answer to "did <c>AcceptWebSocketAsync</c> return".
///
/// It also captures <c>httpContext.User</c>, which is the only place the principal a
/// ticket-authenticated upgrade runs under is observable at the real boundary. Nothing downstream
/// exposes it: <see cref="IWebSocketConnection.AuthorizationInfo"/> is a different object with a
/// different shape, and comparing those would prove nothing about claims.
///
/// It records and returns. It never throws, never blocks and never touches the connection, so
/// adding it to the pipeline cannot change any outcome under test.
/// </remarks>
public sealed class UpgradeRecorder : IWebSocketListener
{
    private readonly ConcurrentQueue<AcceptedUpgrade> _accepted = new();

    /// <summary>
    /// Gets every upgrade the server accepted since the last <see cref="Clear"/>, in order.
    /// </summary>
    public IReadOnlyList<AcceptedUpgrade> Accepted => _accepted.ToArray();

    /// <summary>
    /// Forgets everything recorded so far.
    /// </summary>
    public void Clear() => _accepted.Clear();

    /// <inheritdoc />
    public Task ProcessMessageAsync(WebSocketMessageInfo message) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ProcessWebSocketConnectedAsync(IWebSocketConnection connection, HttpContext httpContext)
    {
        _accepted.Enqueue(new AcceptedUpgrade(
            httpContext?.User?.Claims.Select(claim => new ClaimRecord(claim.Type, claim.Value)).ToArray()
                ?? [],
            (httpContext?.User?.Identity as ClaimsIdentity)?.AuthenticationType,
            connection?.AuthorizationInfo));

        return Task.CompletedTask;
    }

    /// <summary>
    /// One claim, reduced to the pair the parity comparison is about.
    /// </summary>
    /// <param name="Type">The claim type.</param>
    /// <param name="Value">The claim value.</param>
    public sealed record ClaimRecord(string Type, string Value);

    /// <summary>
    /// One accepted upgrade.
    /// </summary>
    /// <param name="Claims">The claims the principal carried, in issue order.</param>
    /// <param name="AuthenticationType">The identity's authentication type, i.e. the scheme.</param>
    /// <param name="AuthorizationInfo">The authorization the connection was built with.</param>
    public sealed record AcceptedUpgrade(
        IReadOnlyList<ClaimRecord> Claims,
        string? AuthenticationType,
        AuthorizationInfo? AuthorizationInfo);
}
