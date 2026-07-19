using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
using Reefin.Model.Session;
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
    private readonly ILogger<PlaybackSessionsController> _logger;

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
    /// <param name="logger">
    /// Issue #43: carries the client-supplied <c>PlaybackAttemptId</c> into the structured log scope
    /// for the duration of the action, so every line emitted while serving a request of one attempt
    /// is joinable to the other requests of that same attempt. Trailing and optional so existing
    /// test constructions keep compiling.
    /// </param>
    public PlaybackSessionsController(
        IPlaybackSessionManager playbackSessionManager,
        IItemLookupService itemLookupService,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IV2PlanStore v2PlanStore,
        IPlaybackLiveStreamResolver liveStreamResolver,
        IPlaybackLiveWiringDiagnosticsStore liveWiringDiagnosticsStore,
        IMediaEncoder mediaEncoder,
        ILogger<PlaybackSessionsController>? logger = null)
    {
        _logger = logger ?? NullLogger<PlaybackSessionsController>.Instance;
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

        using var attemptScope = BeginAttemptScope(request.PlaybackAttemptId);

        var (kind, options) = await ResolveOptions(request, HttpContext.RequestAborted).ConfigureAwait(false);

        var session = _playbackSessionManager.Create(
            new PlaybackSessionRequest(kind, options),
            request.PlaySessionId,
            request.PlaybackAttemptId);
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
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Item or session not found.</response>
    /// <response code="422">The session exists but re-planning it with these options produces no viable plan.</response>
    /// <returns>The updated session's stable decision projection.</returns>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSessionResponse>> ReplacePlaybackSession([FromRoute] PlaybackSessionId id, [FromBody] ReplacePlaybackSessionRequest request)
    {
        PlaybackSessionRequestValidator.Validate(request);

        using var attemptScope = BeginAttemptScope(request.PlaybackAttemptId);

        // PR118: PUT used to have no ownership check at all - a pre-existing gap (§4.2 of the PR117
        // design doc called it out explicitly for this endpoint) - any authenticated caller who knew
        // an id could re-plan someone else's session. Checked against the EXISTING session (not the
        // new request body's own UserId, which a caller fully controls) before doing any planning
        // work, same owner-or-admin semantics as GetPlaybackSessionStream.
        var existingSession = _playbackSessionManager.Get(id);
        if (existingSession is null)
        {
            return NotFound();
        }

        var authorization = EnsureCallerOwnsSessionOrIsAdmin(existingSession);
        if (authorization is not null)
        {
            return authorization;
        }

        var (kind, options) = await ResolveOptions(request, HttpContext.RequestAborted).ConfigureAwait(false);

        // PR #38 (docs/design-playback-v2-lifecycle.md, decided in 2cf777d2): the session was just
        // proven to exist above, so a null here can only mean re-planning produced NO viable plan -
        // a 404 said "unknown id" for what is really an unsatisfiable request, and left the client's
        // track-change path unable to tell the two apart. 404 stays reserved for the unknown id.
        //
        // Issue #43: the SAME attempt id the POST carried, when the client is re-planning inside
        // one attempt. That identity across two different HTTP requests is exactly what a request
        // id could never provide, and is why this is a separate field.
        var session = _playbackSessionManager.Patch(id, new PlaybackSessionRequest(kind, options), request.PlaybackAttemptId);
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
    /// Removes a playback session.
    /// </summary>
    /// <param name="id">The session to remove.</param>
    /// <response code="204">Session removed.</response>
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Session not found.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeletePlaybackSession([FromRoute] PlaybackSessionId id)
    {
        // PR118: same pre-existing gap as PUT (§4.2) - any authenticated caller who knew an id could
        // end someone else's session. Same owner-or-admin semantics as GetPlaybackSessionStream.
        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        var authorization = EnsureCallerOwnsSessionOrIsAdmin(session);
        if (authorization is not null)
        {
            return authorization;
        }

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
    /// <response code="422">The session has no plannable stream - nothing can be served for it.</response>
    /// <returns>The resolved stream descriptor.</returns>
    [HttpGet("{id}/Stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
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
        // (StreamInfo.ToUrl's &ApiKey=) - this endpoint must never hand that token to anyone but the
        // session's own owner or an administrator. PR118: the same check now also guards
        // ReplacePlaybackSession/DeletePlaybackSession - see EnsureCallerOwnsSessionOrIsAdmin.
        var authorization = EnsureCallerOwnsSessionOrIsAdmin(session);
        if (authorization is not null)
        {
            return authorization;
        }

        // §2.3 (mandatory): a session without a PlaySessionId cannot correlate a served URL to the
        // transcoding job lifecycle - emitting one anyway would break that correlation silently.
        if (string.IsNullOrEmpty(session.PlaySessionId))
        {
            return Conflict("Session has no PlaySessionId - PUT a replacement request supplying one before requesting a stream URL.");
        }

        var options = session.Request?.Options;
        var legacyStreamInfo = session.Plan.StreamInfo;
        var mediaSource = legacyStreamInfo?.MediaSource;
        // PR #38: this used to be a second 409 on the same operation, which OpenAPI cannot express -
        // one `responses` entry per status code per operation means two 409s discriminated only by
        // their body are invisible to every generated client. The two conditions are genuinely
        // different: the 409 above is REPAIRABLE by the client (PUT a request carrying a
        // PlaySessionId), this one is structurally unservable, so it is 422.
        if (options is null || legacyStreamInfo is null || mediaSource is null)
        {
            return UnprocessableEntity("Session has no plannable stream - this endpoint only serves sessions planned via Playback/Sessions.");
        }

        // PR118 (moved up from after resolution): a URL without the caller's token would 401 at
        // fetch time (silent client breakage), and a caller authenticated without a bearer token has
        // no business triggering resolution at all - not just receiving a tokenized URL at the end.
        // Checked before any operational effect (resolution, diagnostics, metrics).
        var accessToken = User.GetToken();
        if (string.IsNullOrEmpty(accessToken))
        {
            return Forbid();
        }

        var resolved = _liveStreamResolver.Resolve(
            session.Id,
            legacyStreamInfo,
            mediaSource,
            options.Profile,
            options.ItemId,
            options.DeviceId,
            session.PlaySessionId,
            startTimeTicks,
            options.AlwaysBurnInSubtitleWhenTranscoding);

        // PR118: the legacy fallback path returns the SAME mutable StreamInfo instance retained in
        // session.Plan.StreamInfo (not a fresh projection, unlike the v2-adapter path) - stamping
        // PlaySessionId/StartPositionTicks directly onto it let two concurrent calls with different
        // startTimeTicks race on each other's writes. WithRequestContext copies per-request first:
        // mirrors MediaInfoHelper.SetDeviceSpecificData's own re-stamp, but never mutates the
        // instance the session itself still holds, on either path (v2 or legacy).
        var resolvedStreamInfo = resolved.WithRequestContext(
            session.PlaySessionId,
            startTimeTicks);

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

        // Issue #57: a Remux decision (domain PlaybackMethod.Remux, mapped by
        // PlaybackExecutionPlanAdapter to legacy PlayMethod.DirectStream) announced a container the
        // source file does not have - Container=mp4/MimeType=video/mp4 for a Matroska source - and
        // then served that source file BYTE-IDENTICALLY. Cause: StreamInfo.ToUrl appends
        // "&Static=true" for every IsDirectStream stream (DirectStream OR DirectPlay), and
        // VideosController.GetVideoStream answers Static=true with
        // FileStreamResponseHelpers.GetStaticFileResult(state.MediaPath, ...) - the source file
        // verbatim, under the announced container's MIME type. The decision and the descriptor are
        // both correct (the client declared it cannot decode the source container, which is exactly
        // what the ContainerNotSupported reason means); it is the EXECUTION that must actually
        // remux, so the URL must not ask for the static file.
        //
        // Normalizing PlayMethod to Transcode for URL serialization is not a re-decision: it is
        // exactly what the legacy PlaybackInfo path already does at its own URL-minting site
        // (MediaInfoHelper.SetDeviceSpecificData sets `streamInfo.PlayMethod = PlayMethod.Transcode`
        // before calling ToUrl for mediaSource.TranscodingUrl), which is why that path never
        // exhibited this defect. This v2 descriptor endpoint was the one URL-minting site that
        // skipped the normalization.
        //
        // DirectPlay is deliberately NOT normalized: there Static=true is correct - the announced
        // container IS the source's, so serving the file verbatim is the honest answer.
        //
        // Gated on ServedByV2, so the fix is confined to the v2 path exactly as the defect is. A
        // LEGACY-fallback DirectStream resolved through this same endpoint announces the SOURCE
        // container (measured: the same Matroska session that reports Container=mp4 under v2 reports
        // Container=mkv on legacy fallback), so announced container and served bytes already agree
        // there - it is not this defect, and normalizing it would be an unrequested behaviour change
        // to a path that is not broken. Only the v2 adapter produces the mismatching pair
        // (Container=mp4 for an mkv source) that makes Static=true a lie.
        //
        // Mutating here is safe and deliberately scoped: resolvedStreamInfo is the per-request
        // MemberwiseClone WithRequestContext just produced (see the PR118 note above), never the
        // instance session.Plan.StreamInfo retains, and PlayMethod is a value type. Nothing outside
        // this method observes the change.
        if (outcome is { ServedByV2: true } && resolvedStreamInfo.PlayMethod == PlayMethod.DirectStream)
        {
            resolvedStreamInfo.PlayMethod = PlayMethod.Transcode;
        }

        var descriptor = PlaybackSessionStreamDescriptorMapper.Map(resolvedStreamInfo, servedBy, outcome?.FallbackReason, _mediaEncoder, accessToken);
        return Ok(descriptor);
    }

    /// <summary>
    /// PR118: the owner-or-admin authorization shared by all three verbs that operate on an
    /// existing session (GET Stream, PUT, DELETE) - originally only enforced on GET Stream (PR117),
    /// leaving PUT/DELETE reachable by any authenticated caller who knew the session id (§4.2 of the
    /// PR117 design doc flagged this as a pre-existing, separately tracked gap). Checked against the
    /// session's OWN stored request options, never anything the caller's request body supplies.
    /// </summary>
    /// <param name="session">The session being read, replaced, or deleted.</param>
    /// <returns>
    /// <c>null</c> if the caller may proceed; otherwise the <see cref="ForbidResult"/> to return
    /// as-is.
    /// </returns>
    private ActionResult? EnsureCallerOwnsSessionOrIsAdmin(PlaybackSession session)
    {
        if (User.IsInRole(UserRoles.Administrator))
        {
            return null;
        }

        var options = session.Request?.Options;
        if (options is null || !options.UserId.Equals(User.GetUserId()))
        {
            return Forbid();
        }

        return null;
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

    /// <summary>
    /// Issue #43: opens a structured log scope carrying the attempt id for the rest of the action,
    /// or returns <c>null</c> when the client supplied none — no scope is better than a scope
    /// carrying a null value. Nests inside the per-request scope opened by
    /// <c>RequestCorrelationMiddleware</c> (issue #42), so lines emitted here carry BOTH the attempt
    /// id (same on a retry) and the request id (different on a retry).
    /// </summary>
    private IDisposable? BeginAttemptScope(string? playbackAttemptId)
    {
        if (string.IsNullOrEmpty(playbackAttemptId))
        {
            return null;
        }

        return _logger.BeginScope(new Dictionary<string, object>(1)
        {
            [PlaybackAttemptIdValidator.LogPropertyName] = playbackAttemptId
        });
    }
}
