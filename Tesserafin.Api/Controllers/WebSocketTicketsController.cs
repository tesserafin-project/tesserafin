using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.Models.PlaybackCredentialDtos;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// Mints the single-use ticket that replaces the durable session token in the WebSocket upgrade
/// URL (#153).
/// </summary>
/// <remarks>
/// A ticket is a different type in a different namespace from a playback capability, with a
/// different store, a much shorter life and a consumption rule capabilities do not have. They are
/// deliberately not one mechanism with a flag: a media credential that could also open a socket,
/// or a socket credential that could also fetch media, is a wider grant than either job needs.
/// </remarks>
[Route("WebSocket/Tickets")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class WebSocketTicketsController : BaseTesserafinApiController
{
    private readonly IPlaybackCredentialService _credentialService;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketTicketsController"/> class.
    /// </summary>
    /// <param name="credentialService">The credential store and validator.</param>
    /// <param name="sessionManager">Used to resolve the caller's session, which the ticket binds to.</param>
    /// <param name="userManager">Required by the shared session lookup helper.</param>
    public WebSocketTicketsController(
        IPlaybackCredentialService credentialService,
        ISessionManager sessionManager,
        IUserManager userManager)
    {
        _credentialService = credentialService;
        _sessionManager = sessionManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Mints a WebSocket ticket bound to the caller's session and device.
    /// </summary>
    /// <response code="200">The ticket was minted. Its value is in the body and appears nowhere else.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <returns>The minted ticket.</returns>
    /// <remarks>
    /// Header-authenticated by the durable session token, like every other minting call. The ticket
    /// is consumed by the upgrade handshake that follows immediately, which is why its life is
    /// measured in seconds.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<WebSocketTicketDto>> MintWebSocketTicket()
    {
        Response.Headers.CacheControl = "no-store";

        var session = await RequestHelpers.GetSession(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
        var grant = _credentialService.MintWebSocketTicket(new WebSocketTicketRequest(
            User.GetUserId(),
            session.Id,
            User.GetDeviceId() ?? string.Empty));

        return new WebSocketTicketDto
        {
            TicketId = grant.TicketId,
            Value = grant.Value,
            IssuedAt = grant.IssuedAt,
            ExpiresAt = grant.ExpiresAt
        };
    }
}
