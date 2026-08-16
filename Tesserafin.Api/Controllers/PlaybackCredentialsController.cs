using System;
using System.ComponentModel.DataAnnotations;
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
/// Mints and renews the short-lived, media-scoped playback capability that replaces the durable
/// session token in playback URLs (#153).
/// </summary>
/// <remarks>
/// EVERY ACTION HERE IS HEADER-AUTHENTICATED. <c>[Authorize]</c> resolves the durable session token
/// the ordinary way, which means through the <c>Authorization</c> header for any client that is not
/// already putting it in a URL. Nothing in this controller can be reached by presenting a
/// capability: a capability is not a token, so it never authenticates anything, here least of all.
/// That is what "never minted or renewed through a URL credential" means in practice.
/// </remarks>
[Route("Playback/Capabilities")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class PlaybackCredentialsController : BaseTesserafinApiController
{
    private readonly IPlaybackCredentialService _credentialService;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCredentialsController"/> class.
    /// </summary>
    /// <param name="credentialService">The credential store and validator.</param>
    /// <param name="sessionManager">Used to resolve the caller's session, which the capability binds to.</param>
    /// <param name="userManager">Required by the shared session lookup helper.</param>
    public PlaybackCredentialsController(
        IPlaybackCredentialService credentialService,
        ISessionManager sessionManager,
        IUserManager userManager)
    {
        _credentialService = credentialService;
        _sessionManager = sessionManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Mints a playback capability bound to the caller's session and play session.
    /// </summary>
    /// <param name="request">The item, media source, play session and scopes to bind to.</param>
    /// <response code="200">The capability was minted. Its value is in the body and appears nowhere else.</response>
    /// <response code="400">No scope was requested, or an item-bound scope was requested without an item.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <returns>The minted capability.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PlaybackCapabilityDto>> MintPlaybackCapability(
        [FromBody, Required] PlaybackCapabilityRequestDto request)
    {
        if (request.Scopes.Count == 0)
        {
            return BadRequest();
        }

        // Fonts is the one scope that is not item-bound. Every other scope without an item would be
        // a capability that can never match a demand, which is a silent denial rather than an error.
        foreach (var scope in request.Scopes)
        {
            if (scope != PlaybackCapabilityScope.Fonts && request.ItemId is null)
            {
                return BadRequest();
            }
        }

        // A minted credential is never cached, by anyone, for any duration.
        Response.Headers.CacheControl = "no-store";

        var session = await RequestHelpers.GetSession(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
        var grant = _credentialService.MintCapability(new PlaybackCapabilityRequest(
            User.GetUserId(),
            session.Id,
            User.GetDeviceId() ?? string.Empty,
            request.PlaySessionId,
            request.ItemId,
            request.MediaSourceId,
            request.Scopes));

        return new PlaybackCapabilityDto
        {
            CapabilityId = grant.CapabilityId,
            Value = grant.Value,
            IssuedAt = grant.IssuedAt,
            ExpiresAt = grant.ExpiresAt,
            Scopes = grant.Scopes,
            ItemId = grant.ItemId,
            MediaSourceId = grant.MediaSourceId,
            PlaySessionId = grant.PlaySessionId
        };
    }

    /// <summary>
    /// Extends a capability's expiry, without rotating or re-returning its secret.
    /// </summary>
    /// <param name="capabilityId">The public identifier returned when the capability was minted.</param>
    /// <response code="200">The expiry moved.</response>
    /// <response code="400">Renewal was attempted before the renewal window opened.</response>
    /// <response code="401">The caller is not authenticated, or the capability is unknown, expired or not theirs.</response>
    /// <returns>The new expiry.</returns>
    /// <remarks>
    /// The renewal window is the capability's final minutes. Renewing earlier is refused, because a
    /// client that could renew from the moment of issue would chain a short-lived credential into a
    /// durable one with extra steps — which is the property this whole design removes.
    ///
    /// Renewal after expiry is refused outright rather than silently minting a replacement. An
    /// expired capability is not resurrectable; the client mints a new one with its durable token.
    /// </remarks>
    [HttpPost("{capabilityId}/Renew")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PlaybackCapabilityRenewalDto>> RenewPlaybackCapability([FromRoute, Required] Guid capabilityId)
    {
        Response.Headers.CacheControl = "no-store";

        var session = await RequestHelpers.GetSession(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
        var renewal = _credentialService.RenewCapability(capabilityId, session.Id);

        if (!renewal.Succeeded)
        {
            // "Too early" is the one refusal a correct client can act on, so it is the one refusal
            // that gets its own status. Unknown, expired, revoked and not-yours are deliberately
            // indistinguishable: telling a caller which of those applied tells them which
            // capabilities exist.
            return renewal.Failure == PlaybackCapabilityFailure.RenewalTooEarly
                ? BadRequest()
                : Unauthorized();
        }

        return new PlaybackCapabilityRenewalDto
        {
            CapabilityId = renewal.CapabilityId,
            IssuedAt = renewal.IssuedAt,
            ExpiresAt = renewal.ExpiresAt
        };
    }
}
