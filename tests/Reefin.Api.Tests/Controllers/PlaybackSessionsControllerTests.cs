using System;
using System.Linq;
using System.Reflection;
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
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Api.Tests.Controllers;

public class PlaybackSessionsControllerTests
{
    private readonly Mock<IPlaybackSessionManager> _playbackSessionManager = new();
    private readonly Mock<IItemLookupService> _itemLookupService = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IMediaSourceManager> _mediaSourceManager = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();

    private PlaybackSessionsController CreateController()
    {
        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
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
        _itemLookupService.Setup(m => m.GetItemById<BaseItem>(_itemId)).Returns(item);
        _mediaSourceManager
            .Setup(m => m.GetPlaybackMediaSources(item, It.IsAny<User>(), true, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaSourceInfo { Id = _itemId.ToString("N") }]);
    }

    /// <summary>
    /// Every property of <see cref="PlaybackSessionResponse"/> must be a primitive/BCL value or
    /// drawn from the <see cref="Reefin.Playback.Decision"/> vocabulary (PR91) - never
    /// <c>Reefin.Model.Dlna</c> or the internal <c>Reefin.Controller.MediaEncoding.PlaybackSession</c>
    /// record. Asserted structurally so a future change can't silently reintroduce a leak.
    /// </summary>
    [Fact]
    public void PlaybackSessionResponse_Properties_ReferenceOnlyDecisionVocabOrPrimitives()
    {
        var allowedPrimitiveTypes = new[] { typeof(Guid), typeof(int), typeof(string), typeof(bool), typeof(DateTimeOffset) };

        foreach (var property in typeof(PlaybackSessionResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = property.PropertyType;
            var elementType = GetEnumerableElementTypeOrSelf(type);

            var isAllowed = allowedPrimitiveTypes.Contains(elementType)
                || elementType.Namespace == "Reefin.Playback.Decision";

            Assert.True(isAllowed, $"{property.Name} ({type}) must be a primitive or Reefin.Playback.Decision type.");
        }
    }

    private static Type GetEnumerableElementTypeOrSelf(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsGenericType)
        {
            var definition = underlying.GetGenericTypeDefinition();
            if (definition == typeof(System.Collections.Generic.IReadOnlyList<>))
            {
                return underlying.GetGenericArguments()[0];
            }
        }

        return underlying;
    }

    [Fact]
    public async Task CreatePlaybackSession_ViablePlan_ReturnsMappedResponse()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var streamInfo = new StreamInfo { DeviceProfile = new DeviceProfile(), PlayMethod = PlayMethod.DirectPlay, Container = "mp4" };
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default, streamInfo), default, default);
        _playbackSessionManager
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null))
            .Returns(session);

        var result = await CreateController().CreatePlaybackSession(CreateRequest());

        var response = Assert.IsType<PlaybackSessionResponse>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(session.Id.Value, response.Id);
        Assert.Equal(MediaKind.Video, response.Kind);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, response.DecisionVersion);
        Assert.Equal(PlaybackMethod.DirectPlay, response.Method);
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
        _itemLookupService.Setup(m => m.GetItemById<BaseItem>(_itemId)).Returns((BaseItem?)null);

        await Assert.ThrowsAsync<Reefin.Common.Extensions.ResourceNotFoundException>(
            () => CreateController().CreatePlaybackSession(CreateRequest()));
    }

    [Fact]
    public async Task ReplacePlaybackSession_ViablePlan_ReturnsMappedResponse()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported), default, default);
        _playbackSessionManager
            .Setup(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()))
            .Returns(session);

        var result = await CreateController().ReplacePlaybackSession(session.Id, CreateRequest());

        var response = Assert.IsType<PlaybackSessionResponse>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(PlaybackMethod.Transcode, response.Method);
    }

    [Fact]
    public async Task ReplacePlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        _playbackSessionManager
            .Setup(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()))
            .Returns((PlaybackSession?)null);

        var result = await CreateController().ReplacePlaybackSession(PlaybackSessionId.NewId(), CreateRequest());

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
}
