#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tesserafin.Api.Constants;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Tesserafin.Data;

namespace Tesserafin.Server.Core.HttpServer
{
    public class WebSocketManager : IWebSocketManager
    {
        /// <summary>
        /// The query key a single-use WebSocket ticket travels in (#153).
        /// </summary>
        /// <remarks>
        /// Deliberately not <c>api_key</c>. A ticket is consumed here and nowhere else, so it can
        /// authenticate an upgrade and cannot authenticate an HTTP request of any kind — media or
        /// general — because no HTTP path reads this key.
        /// </remarks>
        public const string TicketQueryKey = "webSocketTicket";

        private readonly IWebSocketListener[] _webSocketListeners;
        private readonly IAuthService _authService;
        private readonly ILogger<WebSocketManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IPlaybackCredentialService _credentialService;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;

        public WebSocketManager(
            IAuthService authService,
            IEnumerable<IWebSocketListener> webSocketListeners,
            ILogger<WebSocketManager> logger,
            ILoggerFactory loggerFactory,
            IPlaybackCredentialService credentialService,
            IUserManager userManager,
            ISessionManager sessionManager)
        {
            _webSocketListeners = webSocketListeners.ToArray();
            _authService = authService;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _credentialService = credentialService;
            _userManager = userManager;
            _sessionManager = sessionManager;
        }

        /// <inheritdoc />
        public async Task WebSocketRequestHandler(HttpContext context)
        {
            var authorizationInfo = await AuthenticateUpgrade(context).ConfigureAwait(false);
            if (!authorizationInfo.IsAuthenticated)
            {
                throw new SecurityException("Token is required");
            }

            try
            {
                _logger.LogInformation("WS {IP} request", context.Connection.RemoteIpAddress);

                WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                var connection = new WebSocketConnection(
                    _loggerFactory.CreateLogger<WebSocketConnection>(),
                    webSocket,
                    authorizationInfo,
                    context.GetNormalizedRemoteIP())
                {
                    RequestUICulture = CultureInfo.CurrentUICulture
                };
                connection.OnReceive = result =>
                {
                    connection.ApplyRequestCulture();
                    return ProcessWebSocketMessageReceived(result);
                };
                await using (connection.ConfigureAwait(false))
                {
                    var tasks = new Task[_webSocketListeners.Length];
                    for (var i = 0; i < _webSocketListeners.Length; ++i)
                    {
                        tasks[i] = _webSocketListeners[i].ProcessWebSocketConnectedAsync(connection, context);
                    }

                    await Task.WhenAll(tasks).ConfigureAwait(false);

                    await connection.ReceiveAsync().ConfigureAwait(false);
                    _logger.LogInformation("WS {IP} closed", context.Connection.RemoteIpAddress);
                }
            }
            catch (Exception ex) // Otherwise ASP.Net will ignore the exception
            {
                _logger.LogError(ex, "WS {IP} WebSocketRequestHandler error", context.Connection.RemoteIpAddress);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                }
            }
        }

        /// <summary>
        /// Authenticates a WebSocket upgrade, preferring a single-use ticket over the durable token.
        /// </summary>
        /// <remarks>
        /// A PRESENTED TICKET IS NEVER ALLOWED TO FALL BACK. If a ticket is present and refused —
        /// unknown, expired, revoked or already consumed — this returns an unauthenticated result
        /// rather than trying the durable token. Falling back would mean a replayed ticket quietly
        /// succeeding whenever the client also happened to send its session token, which is exactly
        /// the silent-fallback failure the contract forbids.
        ///
        /// Consumption happens HERE, before the socket is accepted, not after a successful upgrade.
        /// Consuming on success only would leave the ticket replayable by racing the handshake.
        /// </remarks>
        private async Task<AuthorizationInfo> AuthenticateUpgrade(HttpContext context)
        {
            var presentedTicket = context.Request.Query[TicketQueryKey].ToString();
            if (string.IsNullOrEmpty(presentedTicket))
            {
                return await _authService.Authenticate(context.Request).ConfigureAwait(false);
            }

            var consumed = _credentialService.ConsumeWebSocketTicket(presentedTicket);
            if (!consumed.IsValid)
            {
                return new AuthorizationInfo { IsAuthenticated = false };
            }

            // The ticket names the session it was minted for, and that session already knows its
            // client, version and device name. Reuse them rather than inventing new ones: a session
            // is identified downstream by (deviceId, client, version), so guessing here would make
            // the socket join a DIFFERENT session from the one the ticket is bound to.
            var boundSession = _sessionManager.Sessions.FirstOrDefault(
                session => string.Equals(session.Id, consumed.SessionId, StringComparison.Ordinal));

            var authorizationInfo = new AuthorizationInfo
            {
                IsAuthenticated = true,
                User = _userManager.GetUserById(consumed.UserId),
                DeviceId = consumed.DeviceId,
                Device = boundSession?.DeviceName,
                Client = boundSession?.Client,
                Version = boundSession?.ApplicationVersion,

                // Never the ticket. It is consumed, it is not a bearer credential any more, and
                // putting it here would publish it into the principal's claims and from there into
                // anything that logs them.
                Token = string.Empty,
                IsApiKey = false
            };

            // WHY THE PRINCIPAL IS SET AND NOT JUST RETURNED. Every WebSocket listener resolves its
            // session from context.User, not from this AuthorizationInfo —
            // SessionWebSocketListener.ProcessWebSocketConnectedAsync calls
            // RequestHelpers.GetSession(httpContext), which reads the user id, client, version,
            // device id and device name off the principal. A ticket upgrade that leaves the
            // principal anonymous therefore authenticates the handshake and then produces a socket
            // that is accepted and immediately torn down when the listener cannot resolve it. The
            // durable-token path does not hit this because the authentication middleware has
            // already populated the principal by the time the upgrade runs.
            context.User = BuildTicketPrincipal(authorizationInfo);

            return authorizationInfo;
        }

        /// <summary>
        /// Builds the principal a ticket-authenticated upgrade runs under, carrying the same
        /// internal claims <c>CustomAuthenticationHandler</c> issues for a durable token so that
        /// every downstream reader sees one consistent identity.
        /// </summary>
        /// <param name="authorizationInfo">The identity the consumed ticket resolved to.</param>
        /// <returns>The principal.</returns>
        private static ClaimsPrincipal BuildTicketPrincipal(AuthorizationInfo authorizationInfo)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, authorizationInfo.User?.Username ?? string.Empty),
                new Claim(ClaimTypes.Role, authorizationInfo.User is not null
                    && authorizationInfo.User.HasPermission(Database.Implementations.Enums.PermissionKind.IsAdministrator)
                        ? UserRoles.Administrator
                        : UserRoles.User),
                new Claim(InternalClaimTypes.UserId, authorizationInfo.User?.Id.ToString("N", CultureInfo.InvariantCulture) ?? string.Empty),
                new Claim(InternalClaimTypes.DeviceId, authorizationInfo.DeviceId ?? string.Empty),
                new Claim(InternalClaimTypes.Device, authorizationInfo.Device ?? string.Empty),
                new Claim(InternalClaimTypes.Client, authorizationInfo.Client ?? string.Empty),
                new Claim(InternalClaimTypes.Version, authorizationInfo.Version ?? string.Empty),
                new Claim(InternalClaimTypes.Token, string.Empty),
                new Claim(InternalClaimTypes.IsApiKey, false.ToString(CultureInfo.InvariantCulture))
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims, TicketQueryKey));
        }

        /// <summary>
        /// Processes the web socket message received.
        /// </summary>
        /// <param name="result">The result.</param>
        private async Task ProcessWebSocketMessageReceived(WebSocketMessageInfo result)
        {
            var tasks = new Task[_webSocketListeners.Length];
            for (var i = 0; i < _webSocketListeners.Length; ++i)
            {
                tasks[i] = _webSocketListeners[i].ProcessMessageAsync(result);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
