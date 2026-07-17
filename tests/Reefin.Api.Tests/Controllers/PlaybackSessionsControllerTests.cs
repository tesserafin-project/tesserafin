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
using Reefin.MediaEncoding.Playback;
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
    private readonly Mock<IV2PlanStore> _v2PlanStore = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();

    private PlaybackSessionsController CreateController()
    {
        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object);

        var identity = new ClaimsIdentity([new Claim(InternalClaimTypes.UserId, _userId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        return controller;
    }

    private static Reefin.Playback.Decision.ClientCapabilities CreateCapabilities() => new(
        Decode: new DecodeCapabilities(
            DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
            VideoCodecs: [],
            AudioCodecs: [],
            SubtitleDelivery: [],
            SupportsHls: false,
            SupportsDash: false),
        OutputProfiles: []);

    private static PlaybackConstraints CreateConstraints() => new(
        AllowDirectPlay: true,
        AllowDirectStream: true,
        AllowTranscoding: true,
        AllowVideoStreamCopy: true,
        AllowAudioStreamCopy: true,
        MaxBitrate: null,
        MaxAudioChannels: null,
        PreferredAudioStreamIndex: null,
        PreferredSubtitleStreamIndex: null,
        SubtitleMode: SubtitlePlaybackMode.Default,
        PreferredSubtitleLanguages: [],
        AlwaysBurnInSubtitleWhenTranscoding: false,
        StartTimeTicks: 0);

    private CreatePlaybackSessionRequest CreateRequest(string? playSessionId = null)
        => new(_itemId, _userId, CreateCapabilities(), CreateConstraints(), PlaySessionId: playSessionId);

    private ReplacePlaybackSessionRequest CreateReplaceRequest()
        => new(_itemId, _userId, CreateCapabilities(), CreateConstraints());

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

    /// <summary>
    /// PR113b: <c>ResolveOptions</c> must carry the real requesting user (already resolved via
    /// <c>RequestHelpers.GetUserId</c>) onto <c>MediaOptions.UserId</c>, the only vector that
    /// reaches <c>ShadowPlaybackSessionPlanner</c> - previously left at its default
    /// <see cref="Guid.Empty"/>.
    /// </summary>
    [Fact]
    public async Task CreatePlaybackSession_ResolvesOptions_CarriesRequestingUserId()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        PlaybackSessionRequest? captured = null;
        _playbackSessionManager
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null))
            .Callback<PlaybackSessionRequest, string?>((request, _) => captured = request)
            .Returns(session);

        await CreateController().CreatePlaybackSession(CreateRequest());

        Assert.NotNull(captured);
        Assert.Equal(_userId, captured!.Options.UserId);
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

        var result = await CreateController().ReplacePlaybackSession(session.Id, CreateReplaceRequest());

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

        var result = await CreateController().ReplacePlaybackSession(PlaybackSessionId.NewId(), CreateReplaceRequest());

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
