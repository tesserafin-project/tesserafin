using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Reefin.Api.Helpers;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Common.Api;
using Reefin.Common.Extensions;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Data.Enums;
using Reefin.Model.Dlna;

namespace Reefin.Api.Controllers;

/// <summary>
/// The point-1 v2 playback session protocol: create/patch/delete, exposed alongside
/// <c>/PlaybackInfo</c> (no removal — see docs/major-rewrite-plan-v13.md, milestone J3).
/// Not yet a stable contract: responses are the internal <see cref="PlaybackSession"/> record,
/// same as the read-only diagnostic listing this controller already exposed (J2).
/// </summary>
[Route("System/PlaybackSessions")]
[Authorize]
[Tags("System")]
public class PlaybackSessionsController : BaseReefinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IItemLookupService _itemLookupService;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionsController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="itemLookupService">Instance of the <see cref="IItemLookupService"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    public PlaybackSessionsController(
        IPlaybackSessionManager playbackSessionManager,
        IItemLookupService itemLookupService,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager)
    {
        _playbackSessionManager = playbackSessionManager;
        _itemLookupService = itemLookupService;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <summary>
    /// Gets a snapshot of all currently tracked playback sessions.
    /// </summary>
    /// <response code="200">Playback sessions returned.</response>
    /// <returns>The current sessions.</returns>
    [HttpGet]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PlaybackSession>> GetPlaybackSessions()
    {
        return Ok(_playbackSessionManager.GetAll());
    }

    /// <summary>
    /// Creates (or, if <see cref="CreatePlaybackSessionRequest.PlaySessionId"/> matches an
    /// existing session, replaces) a playback session.
    /// </summary>
    /// <param name="request">The session to plan.</param>
    /// <response code="200">Session created.</response>
    /// <response code="404">Item not found.</response>
    /// <response code="422">No viable playback plan exists for the given options.</response>
    /// <returns>The created session.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSession>> CreatePlaybackSession([FromBody] CreatePlaybackSessionRequest request)
    {
        var (kind, options) = await ResolveOptions(request, CancellationToken.None).ConfigureAwait(false);

        var session = _playbackSessionManager.Create(new PlaybackSessionRequest(kind, options), request.PlaySessionId);
        return session is null
            ? UnprocessableEntity()
            : Ok(session);
    }

    /// <summary>
    /// Re-plans an existing session with new options.
    /// </summary>
    /// <param name="id">The session to patch.</param>
    /// <param name="request">The new options to plan. <see cref="CreatePlaybackSessionRequest.PlaySessionId"/> is ignored — the session's existing id is used.</param>
    /// <response code="200">Session updated.</response>
    /// <response code="404">Item or session not found.</response>
    /// <response code="422">No viable playback plan exists for the given options.</response>
    /// <returns>The updated session.</returns>
    [HttpPatch("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSession>> PatchPlaybackSession([FromRoute] PlaybackSessionId id, [FromBody] CreatePlaybackSessionRequest request)
    {
        var (kind, options) = await ResolveOptions(request, CancellationToken.None).ConfigureAwait(false);

        var session = _playbackSessionManager.Patch(id, new PlaybackSessionRequest(kind, options));
        return session is null
            ? NotFound()
            : Ok(session);
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

    private async Task<(PlaybackMediaKind Kind, MediaOptions Options)> ResolveOptions(CreatePlaybackSessionRequest request, CancellationToken cancellationToken)
    {
        var userId = RequestHelpers.GetUserId(User, request.UserId);
        var user = _userManager.GetUserById(userId) ?? throw new ResourceNotFoundException();
        var item = _itemLookupService.GetItemById<BaseItem>(request.ItemId) ?? throw new ResourceNotFoundException();

        var mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, user, true, true, cancellationToken)
            .ConfigureAwait(false);

        var options = new MediaOptions
        {
            MediaSources = mediaSources.ToArray(),
            Context = EncodingContext.Streaming,
            ItemId = item.Id,
            Profile = request.DeviceProfile,
            MediaSourceId = request.MediaSourceId,
            AudioStreamIndex = request.AudioStreamIndex,
            SubtitleStreamIndex = request.SubtitleStreamIndex,
            MaxAudioChannels = request.MaxAudioChannels,
            MaxBitrate = request.MaxBitrate,
            EnableDirectPlay = request.EnableDirectPlay,
            EnableDirectStream = request.EnableDirectStream,
            AllowVideoStreamCopy = request.AllowVideoStreamCopy,
            AllowAudioStreamCopy = request.AllowAudioStreamCopy,
            AlwaysBurnInSubtitleWhenTranscoding = request.AlwaysBurnInSubtitleWhenTranscoding,
        };

        if (!request.EnableTranscoding)
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
