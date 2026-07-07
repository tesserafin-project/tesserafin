using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Reefin.Common.Api;
using Reefin.Controller.MediaEncoding;

namespace Reefin.Api.Controllers;

/// <summary>
/// Diagnostic endpoint for the sessions tracked by <see cref="IPlaybackSessionManager"/>.
/// Read-only: exists to compare v2 session-planning decisions against <c>/PlaybackInfo</c> in
/// real usage, not as a stable public contract yet (point 1-2 of the major rewrite plan).
/// </summary>
[Route("System/PlaybackSessions")]
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("System")]
public class PlaybackSessionsController : BaseReefinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionsController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    public PlaybackSessionsController(IPlaybackSessionManager playbackSessionManager)
    {
        _playbackSessionManager = playbackSessionManager;
    }

    /// <summary>
    /// Gets a snapshot of all currently tracked playback sessions.
    /// </summary>
    /// <response code="200">Playback sessions returned.</response>
    /// <returns>The current sessions.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PlaybackSession>> GetPlaybackSessions()
    {
        return Ok(_playbackSessionManager.GetAll());
    }
}
