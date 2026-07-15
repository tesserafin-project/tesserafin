using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Reefin.Common.Api;
using Reefin.Controller.MediaEncoding;

namespace Reefin.Api.Controllers;

/// <summary>
/// The admin-only playback diagnostics surface (docs/pr92-design-playback-api-and-diagnostics.md
/// §2): observes currently tracked playback sessions. Split out of the old
/// <c>System/PlaybackSessions</c> route, which mixed this admin listing with the client
/// create/replace/delete verbs under a single controller and route — this GET never shared the
/// client protocol's authorization scope or public, only its route. For this slice (PR112), the
/// response is still the internal <see cref="PlaybackSession"/> record; the richer, filtered
/// diagnostic detail (<c>PlaybackDiagnosticDetail</c>, §4.3 — request context, source snapshot,
/// full reasoning tree, legacy/v2 comparison, timeline) is PR113.
/// </summary>
[Route("System/PlaybackDiagnostics/Sessions")]
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("System")]
public class PlaybackDiagnosticsSessionsController : BaseReefinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackDiagnosticsSessionsController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    public PlaybackDiagnosticsSessionsController(IPlaybackSessionManager playbackSessionManager)
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

    /// <summary>
    /// Gets a single tracked playback session, for diagnostics.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <response code="200">Session returned.</response>
    /// <response code="404">Session not found.</response>
    /// <returns>The session.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PlaybackSession> GetPlaybackSession([FromRoute] PlaybackSessionId id)
    {
        var session = _playbackSessionManager.Get(id);
        return session is null
            ? NotFound()
            : Ok(session);
    }
}
