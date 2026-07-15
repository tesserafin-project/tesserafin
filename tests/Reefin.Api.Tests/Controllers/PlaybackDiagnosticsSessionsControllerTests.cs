using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Reefin.Api.Controllers;
using Reefin.Common.Api;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Session;
using Xunit;

namespace Reefin.Api.Tests.Controllers;

public class PlaybackDiagnosticsSessionsControllerTests
{
    private readonly Mock<IPlaybackSessionManager> _playbackSessionManager = new();

    private PlaybackDiagnosticsSessionsController CreateController()
        => new(_playbackSessionManager.Object);

    /// <summary>
    /// This admin surface must never share the client controller's authorization scope: it
    /// requires elevation at the class level, same as the old <c>System/PlaybackSessions</c> GET
    /// did before the split (docs/pr92-design-playback-api-and-diagnostics.md §2).
    /// </summary>
    [Fact]
    public void Controller_RequiresElevation()
    {
        var attribute = typeof(PlaybackDiagnosticsSessionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal(Policies.RequiresElevation, attribute.Policy);
    }

    [Fact]
    public void GetPlaybackSessions_ReturnsAllTrackedSessions()
    {
        var sessions = new[]
        {
            new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default),
        };
        _playbackSessionManager.Setup(m => m.GetAll()).Returns(sessions);

        var result = CreateController().GetPlaybackSessions();

        Assert.Same(sessions, Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public void GetPlaybackSession_ExistingSession_ReturnsSession()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var result = CreateController().GetPlaybackSession(session.Id);

        Assert.Same(session, Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public void GetPlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = CreateController().GetPlaybackSession(id);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
