using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Reefin.Api.Constants;
using Reefin.Api.Extensions;
using Reefin.Api.Helpers;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Common.Extensions;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Data.Enums;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;
using Reefin.Playback.Dlna;

namespace Reefin.Api.Controllers;

/// <summary>
/// The client-facing playback session protocol (docs/pr92-design-playback-api-and-diagnostics.md
/// §2-3): create, fully replace, and end a session. Responses are the stable, versioned
/// <see cref="PlaybackSessionResponse"/> (PR91 decision vocabulary), never the internal
/// <see cref="PlaybackSession"/> record. The admin-only listing/diagnostic surface this controller
/// used to also expose has moved to <see cref="PlaybackDiagnosticsSessionsController"/> — it never
/// shared this controller's public or authorization scope, only its route. Since PR115a, responses
/// reflect the v2 decision (<c>DecisionVersion</c> = the real engine version) for sessions the
/// canary made v2-authoritative, and the legacy projection otherwise.
/// </summary>
[Route("Playback/Sessions")]
[Authorize]
[Tags("Playback")]
public class PlaybackSessionsController : BaseReefinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IItemLookupService _itemLookupService;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly IPlaybackLiveStreamResolver _liveStreamResolver;
    private readonly IPlaybackLiveWiringDiagnosticsStore _liveWiringDiagnosticsStore;
    private readonly IMediaEncoder _mediaEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionsController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="itemLookupService">Instance of the <see cref="IItemLookupService"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="v2PlanStore">Instance of the <see cref="IV2PlanStore"/> interface (PR115a) — looked up to see whether v2 is authoritative for a session's response.</param>
    /// <param name="liveStreamResolver">
    /// PR117 (docs/pr116d-url-contract-design.md §3.3): the shared live-wiring decision, re-run at
    /// call time on <see cref="GetPlaybackSessionStream"/> - same component, same kill switch/
    /// stop-threshold guard/SourceId verification/Dolby Vision exclusion, that
    /// <c>MediaInfoHelper.SetDeviceSpecificData</c> already consults for the legacy path.
    /// </param>
    /// <param name="liveWiringDiagnosticsStore">
    /// PR117: read back immediately after <paramref name="liveStreamResolver"/> resolves, so the
    /// descriptor's <c>FallbackReason</c> reflects exactly the decision that produced the URL - the
    /// same store the admin diagnostics detail route already surfaces (PR115c).
    /// </param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface - resolves the external subtitle delivery URL for the descriptor.</param>
    public PlaybackSessionsController(
        IPlaybackSessionManager playbackSessionManager,
        IItemLookupService itemLookupService,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IV2PlanStore v2PlanStore,
        IPlaybackLiveStreamResolver liveStreamResolver,
        IPlaybackLiveWiringDiagnosticsStore liveWiringDiagnosticsStore,
        IMediaEncoder mediaEncoder)
    {
        _playbackSessionManager = playbackSessionManager;
        _itemLookupService = itemLookupService;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _v2PlanStore = v2PlanStore;
        _liveStreamResolver = liveStreamResolver;
        _liveWiringDiagnosticsStore = liveWiringDiagnosticsStore;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Creates (or, if <see cref="CreatePlaybackSessionRequest.PlaySessionId"/> matches an
    /// existing session, replaces) a playback session.
    /// </summary>
    /// <param name="request">The session to plan.</param>
    /// <response code="200">Session created.</response>
    /// <response code="400">The declared capabilities or constraints are invalid.</response>
    /// <response code="404">Item not found.</response>
    /// <response code="422">No viable playback plan exists for the given options.</response>
    /// <returns>The created session's stable decision projection.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSessionResponse>> CreatePlaybackSession([FromBody] CreatePlaybackSessionRequest request)
    {
        PlaybackSessionRequestValidator.Validate(request);

        var (kind, options) = await ResolveOptions(request, HttpContext.RequestAborted).ConfigureAwait(false);

        var session = _playbackSessionManager.Create(new PlaybackSessionRequest(kind, options), request.PlaySessionId);
        if (session is null)
        {
            return UnprocessableEntity();
        }

        // PR115a: the response reflects the v2 decision when one is retained and authoritative for
        // this session - see PlaybackSessionResponseMapper.Map(PlaybackSession, V2PlanRecord?).
        _v2PlanStore.TryGet(session.Id, out var v2Record);
        return Ok(PlaybackSessionResponseMapper.Map(session, v2Record));
    }

    /// <summary>
    /// Fully re-plans an existing session with a complete new set of options. Decision v1 (PR92
    /// §3): this replaces the misnamed <c>PATCH</c> the point-1 protocol shipped with, which
    /// already required a complete body — <c>PUT</c> states that honestly.
    /// </summary>
    /// <param name="id">The session to replace.</param>
    /// <param name="request">The complete new options to plan.</param>
    /// <response code="200">Session updated.</response>
    /// <response code="400">The declared capabilities or constraints are invalid.</response>
    /// <response code="404">Item or session not found.</response>
    /// <response code="422">No viable playback plan exists for the given options.</response>
    /// <returns>The updated session's stable decision projection.</returns>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSessionResponse>> ReplacePlaybackSession([FromRoute] PlaybackSessionId id, [FromBody] ReplacePlaybackSessionRequest request)
    {
        PlaybackSessionRequestValidator.Validate(request);

        var (kind, options) = await ResolveOptions(request, HttpContext.RequestAborted).ConfigureAwait(false);

        var session = _playbackSessionManager.Patch(id, new PlaybackSessionRequest(kind, options));
        if (session is null)
        {
            return NotFound();
        }

        // PR115a: the response reflects the v2 decision when one is retained and authoritative for
        // this session - see PlaybackSessionResponseMapper.Map(PlaybackSession, V2PlanRecord?).
        _v2PlanStore.TryGet(session.Id, out var v2Record);
        return Ok(PlaybackSessionResponseMapper.Map(session, v2Record));
    }

    /// <summary>
    /// Removes a playback session.
    /// </summary>
    /// <param name="id">The session to remove.</param>
    /// <response code="204">Session removed.</response>
    /// <response code="404">Session not found.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeletePlaybackSession([FromRoute] PlaybackSessionId id)
    {
        return _playbackSessionManager.Delete(id)
            ? NoContent()
            : NotFound();
    }

    /// <summary>
    /// PR117 (docs/pr116d-url-contract-design.md §2): resolves the executable URL for a session
    /// already planned via <see cref="CreatePlaybackSession"/>/<see cref="ReplacePlaybackSession"/>.
    /// A read, not a decision (§2.2): re-projects <c>session.Plan</c>/<c>session.Request</c> that
    /// already exist, re-evaluating the kill switch/stop-threshold guard/plan resolution at THIS
    /// call (§3.1) - the same live-wiring decision the legacy <c>PlaybackInfo</c> path makes on
    /// every request, never cached from the original <c>POST</c>/<c>PUT</c>.
    /// </summary>
    /// <param name="id">The session to resolve a stream URL for.</param>
    /// <param name="startTimeTicks">
    /// The position, in ticks, playback should start from - a property of the moment a client asks
    /// to read, not of the session's own planning decision (§2.2, option (i)).
    /// </param>
    /// <response code="200">Descriptor resolved.</response>
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Session not found.</response>
    /// <response code="409">The session has no <c>PlaySessionId</c> - re-request via <see cref="ReplacePlaybackSession"/> supplying one.</response>
    /// <returns>The resolved stream descriptor.</returns>
    [HttpGet("{id}/Stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<PlaybackSessionStreamDescriptor> GetPlaybackSessionStream([FromRoute] PlaybackSessionId id, [FromQuery] long startTimeTicks = 0)
    {
        if (startTimeTicks < 0)
        {
            return BadRequest("startTimeTicks must be non-negative.");
        }

        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        // §4.2 (mandatory, new on this endpoint): a URL carries the caller's own access token
        // (StreamInfo.ToUrl's &ApiKey=) - unlike ReplacePlaybackSession/DeletePlaybackSession (a
        // pre-existing, separately tracked gap, §4.2), this endpoint must never hand that token to
        // anyone but the session's own owner or an administrator.
        var options = session.Request?.Options;
        var isAdmin = User.IsInRole(UserRoles.Administrator);
        if (!isAdmin && (options is null || !options.UserId.Equals(User.GetUserId())))
        {
            return Forbid();
        }

        // §2.3 (mandatory): a session without a PlaySessionId cannot correlate a served URL to the
        // transcoding job lifecycle - emitting one anyway would break that correlation silently.
        if (string.IsNullOrEmpty(session.PlaySessionId))
        {
            return Conflict("Session has no PlaySessionId - PUT a replacement request supplying one before requesting a stream URL.");
        }

        var legacyStreamInfo = session.Plan.StreamInfo;
        var mediaSource = legacyStreamInfo?.MediaSource;
        if (options is null || legacyStreamInfo is null || mediaSource is null)
        {
            return Conflict("Session has no plannable stream - this endpoint only serves sessions planned via Playback/Sessions.");
        }

        var resolvedStreamInfo = _liveStreamResolver.Resolve(
            session.Id,
            legacyStreamInfo,
            mediaSource,
            options.Profile,
            options.ItemId,
            options.DeviceId,
            session.PlaySessionId,
            startTimeTicks,
            options.AlwaysBurnInSubtitleWhenTranscoding);

        // Mirrors MediaInfoHelper.SetDeviceSpecificData: re-stamped AFTER resolution, on whichever
        // StreamInfo (v2 or legacy fallback) was actually chosen, so this call's own startTimeTicks
        // always wins over whatever was baked in at the original planning call.
        resolvedStreamInfo.PlaySessionId = session.PlaySessionId;
        resolvedStreamInfo.StartPositionTicks = startTimeTicks;

        // §3: FallbackReason/ServedBy must reflect exactly the decision that produced
        // resolvedStreamInfo, not a separately timed lookup - read back the outcome the resolver
        // itself just recorded (PR115c's own store), then only look up the real engine version when
        // that outcome says v2 actually served this request.
        _liveWiringDiagnosticsStore.TryGet(session.Id, out var outcome);
        var servedBy = PlaybackSessionResponse.LegacyDecisionVersion;
        if (outcome is { ServedByV2: true } && _v2PlanStore.TryGet(session.Id, out var v2Record) && v2Record is not null)
        {
            servedBy = v2Record.Decision.EngineVersion;
        }

        // A URL without the caller's token would 401 at fetch time (silent client breakage) - and a
        // caller authenticated without a bearer token has no business receiving a tokenized URL.
        var accessToken = User.GetToken();
        if (string.IsNullOrEmpty(accessToken))
        {
            return Forbid();
        }

        var descriptor = PlaybackSessionStreamDescriptorMapper.Map(resolvedStreamInfo, servedBy, outcome?.FallbackReason, _mediaEncoder, accessToken);
        return Ok(descriptor);
    }

    private async Task<(PlaybackMediaKind Kind, MediaOptions Options)> ResolveOptions(PlaybackPlanRequestBase request, CancellationToken cancellationToken)
    {
        var userId = RequestHelpers.GetUserId(User, request.UserId);
        var user = _userManager.GetUserById(userId) ?? throw new ResourceNotFoundException();
        var item = _itemLookupService.GetItemById<BaseItem>(request.ItemId) ?? throw new ResourceNotFoundException();

        var mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, user, true, true, cancellationToken)
            .ConfigureAwait(false);

        // PR112b: the request now carries ClientCapabilities/PlaybackConstraints (PR91 decision
        // vocabulary), never a raw DeviceProfile - ReverseDlnaAdapter is the TEMPORARY translation
        // back to what the legacy pipeline still consumes (delete alongside it - PR114a).
        var options = new MediaOptions
        {
            MediaSources = mediaSources.ToArray(),
            Context = EncodingContext.Streaming,
            ItemId = item.Id,
            UserId = userId,
            Profile = ReverseDlnaAdapter.ToDeviceProfile(request.Capabilities),
            MediaSourceId = request.MediaSourceId,
            // PR115a: the canary cohort key is deterministic on user+device, so v2 authority for a
            // session depends on the requesting device being identifiable here.
            DeviceId = User.GetDeviceId(),
        };
        ReverseDlnaAdapter.ApplyConstraints(options, request.Constraints);

        if (!request.Constraints.AllowTranscoding)
        {
            foreach (var mediaSource in mediaSources)
            {
                mediaSource.SupportsTranscoding = false;
            }
        }

        var kind = item.MediaType == MediaType.Audio ? PlaybackMediaKind.Audio : PlaybackMediaKind.Video;
        return (kind, options);
    }
}
