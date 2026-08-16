using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.Models.PlaybackCredentialDtos;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
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
    private readonly IItemAccessService _itemAccessService;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ITranscodeManager _transcodeManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCredentialsController"/> class.
    /// </summary>
    /// <param name="credentialService">The credential store and validator.</param>
    /// <param name="sessionManager">Used to resolve the caller's session, which the capability binds to.</param>
    /// <param name="userManager">Resolves the caller, whose visibility rules the item is checked against.</param>
    /// <param name="itemAccessService">Resolves an item only if this user is allowed to see it.</param>
    /// <param name="mediaSourceManager">Enumerates the item's own media sources.</param>
    /// <param name="transcodeManager">Knows which device owns a play session, when the server knows at all.</param>
    public PlaybackCredentialsController(
        IPlaybackCredentialService credentialService,
        ISessionManager sessionManager,
        IUserManager userManager,
        IItemAccessService itemAccessService,
        IMediaSourceManager mediaSourceManager,
        ITranscodeManager transcodeManager)
    {
        _credentialService = credentialService;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _itemAccessService = itemAccessService;
        _mediaSourceManager = mediaSourceManager;
        _transcodeManager = transcodeManager;
    }

    /// <summary>
    /// Mints a playback capability bound to the caller's session and play session.
    /// </summary>
    /// <param name="request">The item, media source, play session and scopes to bind to.</param>
    /// <response code="200">The capability was minted. Its value is in the body and appears nowhere else.</response>
    /// <response code="400">The requested scope set is not one a capability can satisfy.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="404">The item, the media source or the play session is not this caller's to name.</response>
    /// <returns>The minted capability.</returns>
    /// <remarks>
    /// WHY THE CHECKS ARE HERE AND NOWHERE ELSE. <c>StreamingHelpers.GetStreamingState</c> reads the
    /// user id off the principal and then never asks whether that user may see the item: no library
    /// restriction, no blocked tag, no media-source ownership check runs anywhere on the delivery
    /// path. Whatever a capability is permitted to name here is therefore what it can fetch for its
    /// whole lifetime, so this is the only place those restrictions can hold at all. Remote access
    /// and the parental schedule are the exception and are deliberately NOT re-checked here:
    /// <c>MediaDeliveryRequirement</c> subclasses <c>DefaultAuthorizationRequirement</c>, so
    /// <c>DefaultAuthorizationHandler</c> re-evaluates both on every delivery request, for a
    /// capability principal exactly as for a durable token. Checking them twice is how two code
    /// paths drift into disagreeing.
    ///
    /// WHY REFUSALS ARE 404 AND NOT 403. "You may not see this item" and "there is no such item"
    /// have to be indistinguishable, or the endpoint becomes an oracle for which items exist on a
    /// server the caller cannot browse.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaybackCapabilityDto>> MintPlaybackCapability(
        [FromBody, Required] PlaybackCapabilityRequestDto request)
    {
        if (request.Scopes.Count == 0)
        {
            return BadRequest();
        }

        // An undefined enum value is not a scope. Model binding accepts any integer for an enum, so
        // without this a capability could be minted carrying a scope no route will ever demand —
        // which reads as a valid credential and grants nothing, the worst of both.
        foreach (var scope in request.Scopes)
        {
            if (!Enum.IsDefined(scope))
            {
                return BadRequest();
            }
        }

        // Fonts is the one item-less scope, and since binding is now compared exactly, that makes
        // the two halves mutually exclusive: font routes name no item and every other media route
        // names one, so a set holding both can never satisfy both. Minting it would hand back a
        // credential that is silently half-dead.
        var namesFonts = request.Scopes.Contains(PlaybackCapabilityScope.Fonts);
        var namesItemBoundScope = request.Scopes.Any(scope => scope != PlaybackCapabilityScope.Fonts);

        if (namesFonts && namesItemBoundScope)
        {
            return BadRequest();
        }

        if (namesFonts && (request.ItemId is not null || request.MediaSourceId is not null))
        {
            return BadRequest();
        }

        if (namesItemBoundScope && request.ItemId is null)
        {
            return BadRequest();
        }

        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        if (request.ItemId is not null)
        {
            // The same predicate the ordinary playback path uses. Parental and library restrictions
            // are this call, not a second implementation of them.
            var item = _itemAccessService.GetVisibleItemById<BaseItem>(request.ItemId.Value, user);
            if (item is null)
            {
                return NotFound();
            }

            if (request.MediaSourceId is not null)
            {
                var sources = await _mediaSourceManager
                    .GetPlaybackMediaSources(item, user, false, false, HttpContext.RequestAborted)
                    .ConfigureAwait(false);

                if (!sources.Any(source => string.Equals(source.Id, request.MediaSourceId, StringComparison.Ordinal)))
                {
                    return NotFound();
                }
            }
        }

        // A play session the server already knows about belongs to a device. Naming someone else's
        // is a claim about a playback that is not the caller's. A play session the server has never
        // heard of is the ordinary direct-play case — the client chose the identifier and no
        // transcoding job carries it — so there is nothing to compare and the binding is enforced
        // at delivery instead.
        var job = _transcodeManager.GetTranscodingJob(request.PlaySessionId);
        if (job is not null && !string.Equals(job.DeviceId, User.GetDeviceId(), StringComparison.Ordinal))
        {
            return NotFound();
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
