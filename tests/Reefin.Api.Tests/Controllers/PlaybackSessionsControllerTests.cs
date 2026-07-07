using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Reefin.Api.Constants;
using Reefin.Api.Controllers;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Session;
using Xunit;

namespace Reefin.Api.Tests.Controllers;

public class PlaybackSessionsControllerTests
{
    private readonly Mock<IPlaybackSessionManager> _playbackSessionManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IMediaSourceManager> _mediaSourceManager = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();

    private PlaybackSessionsController CreateController()
    {
        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _libraryManager.Object,
            _userManager.Object,
            _mediaSourceManager.Object);

        var identity = new ClaimsIdentity([new Claim(InternalClaimTypes.UserId, _userId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        return controller;
    }

    private CreatePlaybackSessionRequest CreateRequest(string? playSessionId = null)
        => new(_itemId, _userId, new DeviceProfile(), PlaySessionId: playSessionId);

    private void SetUpItemAndUser(Video item)
    {
        _userManager.Setup(m => m.GetUserById(_userId)).Returns(new User("test", "auth", "reset"));
        _libraryManager.Setup(m => m.GetItemById<BaseItem>(_itemId)).Returns(item);
        _mediaSourceManager
            .Setup(m => m.GetPlaybackMediaSources(item, It.IsAny<User>(), true, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaSourceInfo { Id = _itemId.ToString("N") }]);
    }

    [Fact]
    public async Task CreatePlaybackSession_ViablePlan_ReturnsSession()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var expected = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null))
            .Returns(expected);

        var result = await CreateController().CreatePlaybackSession(CreateRequest());

        Assert.Equal(expected, Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task CreatePlaybackSession_NoViablePlan_ReturnsUnprocessableEntity()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        _playbackSessionManager
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null))
            .Returns((PlaybackSession?)null);

        var result = await CreateController().CreatePlaybackSession(CreateRequest());

        Assert.IsType<UnprocessableEntityResult>(result.Result);
    }

    [Fact]
    public async Task CreatePlaybackSession_ItemNotFound_Throws()
    {
        _userManager.Setup(m => m.GetUserById(_userId)).Returns(new User("test", "auth", "reset"));
        _libraryManager.Setup(m => m.GetItemById<BaseItem>(_itemId)).Returns((BaseItem?)null);

        await Assert.ThrowsAsync<Reefin.Common.Extensions.ResourceNotFoundException>(
            () => CreateController().CreatePlaybackSession(CreateRequest()));
    }

    [Fact]
    public async Task PatchPlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        _playbackSessionManager
            .Setup(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()))
            .Returns((PlaybackSession?)null);

        var result = await CreateController().PatchPlaybackSession(PlaybackSessionId.NewId(), CreateRequest());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void DeletePlaybackSession_ExistingSession_ReturnsNoContent()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(true);

        var result = CreateController().DeletePlaybackSession(id);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public void DeletePlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(false);

        var result = CreateController().DeletePlaybackSession(id);

        Assert.IsType<NotFoundResult>(result);
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
}
