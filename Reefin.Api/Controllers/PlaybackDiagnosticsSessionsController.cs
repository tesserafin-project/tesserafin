using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Common.Api;
using Reefin.Controller.MediaEncoding;
using Reefin.MediaEncoding.Playback;

namespace Reefin.Api.Controllers;

/// <summary>
/// The admin-only playback diagnostics surface (docs/pr92-design-playback-api-and-diagnostics.md
/// §2): observes currently tracked playback sessions. Split out of the old
/// <c>System/PlaybackSessions</c> route, which mixed this admin listing with the client
/// create/replace/delete verbs under a single controller and route — this GET never shared the
/// client protocol's authorization scope or public, only its route. PR113: both routes now return
/// filtered projections (<see cref="PlaybackSessionListItem"/>, <see cref="PlaybackDiagnosticDetail"/>)
/// rather than the internal <see cref="PlaybackSession"/> record, closing the
/// <c>MediaSourceInfo.Path</c>/<c>OpenToken</c>/<c>TranscodingUrl</c> leak that returning it directly
/// would otherwise carry.
/// </summary>
[Route("System/PlaybackDiagnostics/Sessions")]
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("System")]
public class PlaybackDiagnosticsSessionsController : BaseReefinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IShadowDiagnosticsStore _diagnosticsStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackDiagnosticsSessionsController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="diagnosticsStore">Instance of the <see cref="IShadowDiagnosticsStore"/> interface.</param>
    public PlaybackDiagnosticsSessionsController(IPlaybackSessionManager playbackSessionManager, IShadowDiagnosticsStore diagnosticsStore)
    {
        _playbackSessionManager = playbackSessionManager;
        _diagnosticsStore = diagnosticsStore;
    }

    /// <summary>
    /// Gets a snapshot of all currently tracked playback sessions.
    /// </summary>
    /// <response code="200">Playback sessions returned.</response>
    /// <returns>The current sessions, each flagged with whether a richer diagnostic is available.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PlaybackSessionListItem>> GetPlaybackSessions()
    {
        IReadOnlyList<PlaybackSessionListItem> items = _playbackSessionManager.GetAll()
            .Select(session => new PlaybackSessionListItem(
                PlaybackSessionResponseMapper.Map(session),
                _diagnosticsStore.TryGet(session.Id, out _)))
            .ToList();

        return Ok(items);
    }

    /// <summary>
    /// Gets a single tracked playback session's diagnostic detail.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <response code="200">Session found; detail returned. The v2-sourced fields are populated only when a diagnostic was retained.</response>
    /// <response code="404">Session not found.</response>
    /// <returns>The filtered diagnostic detail.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PlaybackDiagnosticDetail> GetPlaybackSession([FromRoute] PlaybackSessionId id)
    {
        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        _diagnosticsStore.TryGet(id, out var diagnostic);
        return Ok(PlaybackDiagnosticDetailMapper.Map(session, diagnostic));
    }
}
