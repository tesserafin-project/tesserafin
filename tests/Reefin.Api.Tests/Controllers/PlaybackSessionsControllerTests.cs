using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Reefin.Api.Constants;
using Reefin.Api.Controllers;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Entities;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Configuration;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Model.Session;
using Reefin.Playback.Decision;
using Reefin.Playback.Dlna;
using Reefin.Playback.Execution;
using Xunit;

namespace Reefin.Api.Tests.Controllers;

public class PlaybackSessionsControllerTests
{
    private readonly Mock<IPlaybackSessionManager> _playbackSessionManager = new();
    private readonly Mock<IItemLookupService> _itemLookupService = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IMediaSourceManager> _mediaSourceManager = new();
    private readonly Mock<IV2PlanStore> _v2PlanStore = new();
    private readonly Mock<IPlaybackLiveStreamResolver> _liveStreamResolver = new();
    private readonly Mock<IPlaybackLiveWiringDiagnosticsStore> _liveWiringDiagnosticsStore = new();
    private readonly Mock<IMediaEncoder> _mediaEncoder = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();

    private PlaybackSessionsController CreateController()
    {
        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object,
            _liveStreamResolver.Object,
            _liveWiringDiagnosticsStore.Object,
            _mediaEncoder.Object);

        SetIdentity(controller, _userId);

        return controller;
    }

    private static void SetIdentity(PlaybackSessionsController controller, Guid userId, bool isAdmin = false, string? token = "caller-token")
    {
        var claims = new List<Claim> { new(InternalClaimTypes.UserId, userId.ToString()) };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, UserRoles.Administrator));
        }

        if (token is not null)
        {
            claims.Add(new Claim(InternalClaimTypes.Token, token));
        }

        var identity = new ClaimsIdentity(claims, "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
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
            .Setup(m => m.Create(It.IsAny<PlaybackSessionRequest>(), null, null))
            .Callback<PlaybackSessionRequest, string?, string?>((request, _, _) => captured = request)
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

    /// <summary>
    /// A minimal existing session shaped only for the owner-or-admin check
    /// (<c>EnsureCallerOwnsSessionOrIsAdmin</c>) that PUT/DELETE now perform against whatever
    /// <see cref="IPlaybackSessionManager.Get"/> returns for the target id - independent of whatever
    /// the verb's own mocked outcome (<c>Patch</c>/<c>Delete</c>) later returns.
    /// </summary>
    private PlaybackSession BuildExistingSessionForAuth(PlaybackSessionId id, Guid ownerId)
    {
        var options = new MediaOptions { ItemId = _itemId, UserId = ownerId, Profile = new DeviceProfile() };
        return new PlaybackSession(id, PlaybackMediaKind.Video, null, new PlaybackSessionRequest(PlaybackMediaKind.Video, options), new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
    }

    [Fact]
    public async Task ReplacePlaybackSession_ViablePlan_ReturnsMappedResponse()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(BuildExistingSessionForAuth(session.Id, _userId));
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
        _playbackSessionManager.Setup(m => m.Get(It.IsAny<PlaybackSessionId>())).Returns((PlaybackSession?)null);

        var result = await CreateController().ReplacePlaybackSession(PlaybackSessionId.NewId(), CreateReplaceRequest());

        Assert.IsType<NotFoundResult>(result.Result);
        _playbackSessionManager.Verify(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()), Times.Never);
    }

    /// <summary>
    /// PR118: PUT used to have no ownership check at all - any authenticated caller who knew the id
    /// could re-plan someone else's session. Same owner-or-admin semantics as
    /// <c>GetPlaybackSessionStream</c> (§4.2 of the PR117 design doc flagged this as a pre-existing,
    /// separately tracked gap).
    /// </summary>
    [Fact]
    public async Task ReplacePlaybackSession_OtherUser_ReturnsForbidden()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, Guid.NewGuid()));

        var result = await CreateController().ReplacePlaybackSession(id, CreateReplaceRequest());

        Assert.IsType<ForbidResult>(result.Result);
        _playbackSessionManager.Verify(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()), Times.Never);
    }

    /// <summary>
    /// PR118: an administrator may replace a session it does not own - same elevated allowance
    /// <c>GetPlaybackSessionStream</c> already grants.
    /// </summary>
    [Fact]
    public async Task ReplacePlaybackSession_Admin_ReturnsMappedResponseForOtherUsersSession()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, Guid.NewGuid()));
        var patchedSession = new PlaybackSession(id, PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported), default, default);
        _playbackSessionManager.Setup(m => m.Patch(id, It.IsAny<PlaybackSessionRequest>())).Returns(patchedSession);

        var controller = CreateController();
        SetIdentity(controller, Guid.NewGuid(), isAdmin: true);

        var result = await controller.ReplacePlaybackSession(id, CreateReplaceRequest());

        Assert.IsAssignableFrom<OkObjectResult>(result.Result);
    }

    [Fact]
    public void DeletePlaybackSession_ExistingSession_ReturnsNoContent()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, _userId));
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(true);

        var result = CreateController().DeletePlaybackSession(id);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public void DeletePlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = CreateController().DeletePlaybackSession(id);

        Assert.IsType<NotFoundResult>(result);
        _playbackSessionManager.Verify(m => m.Delete(It.IsAny<PlaybackSessionId>()), Times.Never);
    }

    /// <summary>
    /// PR118: DELETE used to have no ownership check at all - any authenticated caller who knew the
    /// id could end someone else's session. Same owner-or-admin semantics as
    /// <c>GetPlaybackSessionStream</c>.
    /// </summary>
    [Fact]
    public void DeletePlaybackSession_OtherUser_ReturnsForbidden()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, Guid.NewGuid()));

        var result = CreateController().DeletePlaybackSession(id);

        Assert.IsType<ForbidResult>(result);
        _playbackSessionManager.Verify(m => m.Delete(It.IsAny<PlaybackSessionId>()), Times.Never);
    }

    /// <summary>
    /// PR118: an administrator may delete a session it does not own - same elevated allowance
    /// <c>GetPlaybackSessionStream</c> already grants.
    /// </summary>
    [Fact]
    public void DeletePlaybackSession_Admin_ReturnsNoContentForOtherUsersSession()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, Guid.NewGuid()));
        _playbackSessionManager.Setup(m => m.Delete(id)).Returns(true);

        var controller = CreateController();
        SetIdentity(controller, Guid.NewGuid(), isAdmin: true);

        var result = controller.DeletePlaybackSession(id);

        Assert.IsType<NoContentResult>(result);
    }

    private PlaybackSession BuildStreamableSession(Guid ownerId, string? playSessionId, StreamInfo? streamInfo = null)
    {
        var mediaSource = new MediaSourceInfo { Id = "source-1", Container = "mkv", SupportsDirectPlay = true };
        streamInfo ??= new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = mediaSource,
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            AudioStreamIndex = 1,
        };
        var options = new MediaOptions { ItemId = _itemId, UserId = ownerId, DeviceId = "device-1", Profile = new DeviceProfile() };
        return new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            playSessionId,
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.DirectPlay, default, streamInfo),
            default,
            default);
    }

    [Fact]
    public void GetPlaybackSessionStream_NegativeStartTimeTicks_ReturnsBadRequest()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var result = CreateController().GetPlaybackSessionStream(session.Id, startTimeTicks: -1);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// A caller authenticated without a bearer token must not receive a descriptor: the URL would
    /// lack its <c>&amp;ApiKey=</c> and 401 at fetch time, and a token-less principal has no
    /// business receiving a tokenized URL at all. PR118: the check now runs BEFORE resolution -
    /// verified here by asserting the resolver is never invoked and diagnostics are never read, not
    /// just that the response is a 403.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_CallerWithoutToken_ReturnsForbidden()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var controller = CreateController();
        SetIdentity(controller, _userId, token: null);

        var result = controller.GetPlaybackSessionStream(session.Id);

        Assert.IsType<ForbidResult>(result.Result);
        _liveStreamResolver.Verify(
            r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()),
            Times.Never);
        _liveWiringDiagnosticsStore.Verify(
            s => s.TryGet(It.IsAny<PlaybackSessionId>(), out It.Ref<PlaybackLiveWiringOutcome?>.IsAny),
            Times.Never);
    }

    [Fact]
    public void GetPlaybackSessionStream_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = CreateController().GetPlaybackSessionStream(id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Design doc §4.2 (mandatory, new on this endpoint): an authenticated user who is neither the
    /// session's owner nor an administrator must never receive the descriptor - it carries the
    /// caller's own access token in <see cref="PlaybackSessionStreamDescriptor.Url"/>.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_OtherUser_ReturnsForbidden()
    {
        var session = BuildStreamableSession(Guid.NewGuid(), "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public void GetPlaybackSessionStream_Owner_ReturnsDescriptor()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(session.Id, session.Plan.StreamInfo!, It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), _itemId, "device-1", "play-session-1", 0, false))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.NotNull(descriptor.Url);
    }

    /// <summary>
    /// PR118 regression: the legacy fallback path returns the SAME <see cref="StreamInfo"/> instance
    /// retained in <c>session.Plan.StreamInfo</c> (not a fresh projection) - two concurrent calls
    /// with different <c>startTimeTicks</c> used to race on that shared instance's
    /// <see cref="StreamInfo.PlaySessionId"/>/<see cref="StreamInfo.StartPositionTicks"/> before the
    /// URL was built. Each response's own <c>startTimeTicks</c> must always show up in ITS OWN URL,
    /// never the other call's, and the instance the session itself retains must come out completely
    /// untouched by either call. Uses separate controller instances per call - exactly like ASP.NET's
    /// own per-request controller lifetime - racing on the single shared mocked session/StreamInfo,
    /// the actual source of the bug.
    /// </summary>
    [Fact]
    public async Task GetPlaybackSessionStream_ConcurrentCallsWithDifferentTicks_DoNotMutateSharedStreamInfo()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        var sharedStreamInfo = session.Plan.StreamInfo!;
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(session.Id, sharedStreamInfo, It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(sharedStreamInfo);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Fallback(PlaybackLiveFallbackReason.KillSwitch, DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        // Sanity: nothing has touched the shared instance yet.
        Assert.Equal(0, sharedStreamInfo.StartPositionTicks);
        Assert.Null(sharedStreamInfo.PlaySessionId);

        const long ticksA = 1000;
        const long ticksB = 999999;
        PlaybackSessionStreamDescriptor? descriptorA = null;
        PlaybackSessionStreamDescriptor? descriptorB = null;
        var barrier = new Barrier(2);
        var cancellationToken = TestContext.Current.CancellationToken;

        var taskA = Task.Run(
            () =>
            {
                var controller = CreateController();
                barrier.SignalAndWait(cancellationToken);
                var result = controller.GetPlaybackSessionStream(session.Id, ticksA);
                descriptorA = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
            },
            cancellationToken);
        var taskB = Task.Run(
            () =>
            {
                var controller = CreateController();
                barrier.SignalAndWait(cancellationToken);
                var result = controller.GetPlaybackSessionStream(session.Id, ticksB);
                descriptorB = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
            },
            cancellationToken);

        await Task.WhenAll(taskA, taskB);

        Assert.NotNull(descriptorA);
        Assert.NotNull(descriptorB);
        Assert.Contains($"&StartTimeTicks={ticksA}", descriptorA!.Url, StringComparison.Ordinal);
        Assert.Contains($"&StartTimeTicks={ticksB}", descriptorB!.Url, StringComparison.Ordinal);
        Assert.DoesNotContain($"&StartTimeTicks={ticksB}", descriptorA!.Url, StringComparison.Ordinal);
        Assert.DoesNotContain($"&StartTimeTicks={ticksA}", descriptorB!.Url, StringComparison.Ordinal);

        // The instance the session itself still holds must never have been mutated by either call.
        Assert.Equal(0, sharedStreamInfo.StartPositionTicks);
        Assert.Null(sharedStreamInfo.PlaySessionId);
    }

    /// <summary>
    /// Design doc §4.2: an administrator may resolve a stream URL for a session it does not own -
    /// same elevated allowance <c>RequestHelpers.GetUserId</c> already grants elsewhere.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_Admin_ReturnsDescriptorForOtherUsersSession()
    {
        var session = BuildStreamableSession(Guid.NewGuid(), "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(session.Id, It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var controller = CreateController();
        SetIdentity(controller, Guid.NewGuid(), isAdmin: true);

        var result = controller.GetPlaybackSessionStream(session.Id);

        Assert.IsAssignableFrom<OkObjectResult>(result.Result);
    }

    /// <summary>
    /// Design doc §2.3 (mandatory): a session with no <c>PlaySessionId</c> cannot correlate a served
    /// URL to the transcoding job lifecycle - the endpoint must refuse with <c>409</c>, not emit one.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_NoPlaySessionId_ReturnsConflict()
    {
        var session = BuildStreamableSession(_userId, playSessionId: null);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
        _liveStreamResolver.Verify(
            r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()),
            Times.Never);
    }

    /// <summary>
    /// PR #38 (amended, 2cf777d2): the endpoint used to answer <c>409</c> for BOTH "no PlaySessionId"
    /// and "no plannable stream". OpenAPI admits exactly one <c>responses</c> entry per status code
    /// per operation, so two 409s discriminated only by their body cannot exist in the contract and
    /// are invisible to every generated client. The conditions differ in kind - the 409 above is
    /// repairable by a <c>PUT</c> supplying a <c>PlaySessionId</c>, this one is structurally
    /// unservable - so this one is <c>422</c>. Asserted for each of the three shapes that reach it.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void GetPlaybackSessionStream_NoPlannableStream_ReturnsUnprocessableEntity(bool noOptions, bool noStreamInfo, bool noMediaSource)
    {
        StreamInfo? streamInfo = noStreamInfo
            ? null
            : new StreamInfo
            {
                ItemId = _itemId,
                DeviceProfile = new DeviceProfile(),
                PlayMethod = PlayMethod.DirectPlay,
                Container = "mkv",
                MediaSource = noMediaSource ? null : new MediaSourceInfo { Id = "source-1" },
            };

        var request = noOptions
            ? null
            : new PlaybackSessionRequest(PlaybackMediaKind.Video, new MediaOptions { ItemId = _itemId, UserId = _userId, DeviceId = "device-1", Profile = new DeviceProfile() });

        var session = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            "play-session-1",
            request,
            new PlaybackPlan(PlayMethod.DirectPlay, default, streamInfo),
            default,
            default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var controller = CreateController();
        // A null Request means EnsureCallerOwnsSessionOrIsAdmin has no owner to compare against, so
        // only an administrator gets far enough to observe the 422 in that shape.
        SetIdentity(controller, _userId, isAdmin: true);

        var result = controller.GetPlaybackSessionStream(session.Id);

        Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        _liveStreamResolver.Verify(
            r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()),
            Times.Never);
    }

    /// <summary>
    /// PR #38 (amended, 2cf777d2): the session was already proven to exist by the ownership check, so
    /// a null from <c>Patch</c> can only mean re-planning produced no viable plan. That used to be a
    /// <c>404</c>, indistinguishable from an unknown id - precisely the distinction a client needs on
    /// a track change. <c>404</c> stays reserved for the unknown id
    /// (<see cref="ReplacePlaybackSession_UnknownSession_ReturnsNotFound"/>).
    /// </summary>
    [Fact]
    public async Task ReplacePlaybackSession_ExistingSessionNoViablePlan_ReturnsUnprocessableEntity()
    {
        var item = new Movie { Id = _itemId };
        SetUpItemAndUser(item);
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns(BuildExistingSessionForAuth(id, _userId));
        _playbackSessionManager
            .Setup(m => m.Patch(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackSessionRequest>()))
            .Returns((PlaybackSession?)null);

        var result = await CreateController().ReplacePlaybackSession(id, CreateReplaceRequest());

        Assert.IsType<UnprocessableEntityResult>(result.Result);
    }

    /// <summary>
    /// Issue #44 §8 arbitrage A: the server picked a container and reported it nowhere, so the client
    /// had to borrow the legacy <c>TranscodingContainer</c> or fall back to legacy entirely. The
    /// descriptor now carries the effective output container and its content type, taken from the
    /// very <see cref="StreamInfo"/> that produced <c>Url</c>.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_Http_ReportsEffectiveContainerAndMimeType()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal("mkv", descriptor.Container);
        Assert.Equal("video/x-matroska", descriptor.MimeType);
        // The container reported is the one the URL itself carries - that is the whole point.
        Assert.Contains("/stream.mkv", descriptor.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// On HLS the URL addresses <c>master.m3u8</c> while <c>Container</c> is the SEGMENT container
    /// (<c>&amp;SegmentContainer=</c>), so the reported type must be the playlist's, never the
    /// segments'.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_Hls_ReportsSegmentContainerAndPlaylistMimeType()
    {
        var hlsStreamInfo = new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = new MediaSourceInfo { Id = "source-1", Container = "mkv" },
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.Transcode,
            Container = "ts",
            SubProtocol = MediaStreamProtocol.hls,
        };
        var session = BuildStreamableSession(_userId, "play-session-1", hlsStreamInfo);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(hlsStreamInfo);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(StreamingProtocol.Hls, descriptor.Protocol);
        Assert.Equal("ts", descriptor.Container);
        Assert.Equal("application/vnd.apple.mpegurl", descriptor.MimeType);
    }

    /// <summary>
    /// Design doc §3: <c>ServedBy</c> must be the real v2 engine version - read back from the same
    /// <see cref="IV2PlanStore"/> record whose <see cref="V2PlanRecord.ExecutionPlan"/> the resolver
    /// used - only when the live-wiring outcome the resolver itself just recorded says v2 served
    /// this request; <c>FallbackReason</c> must be <see langword="null"/> in that case.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_ServedByV2_ReflectsRealEngineVersionAndNullFallbackReason()
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var selectedStreams = new SelectedStreams(0, 1, null);
        var output = new OutputSpec("mkv", "h264", "aac", null, null, null, null, null, null, StreamingProtocol.Http, null);
        var reasoning = ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method());
        var decision = PlaybackDecision.DirectPlay("source-1", selectedStreams, output, reasoning, engineVersion: 6);
        V2PlanRecord? v2Record = new V2PlanRecord(decision, ExecutionPlan: null, DateTimeOffset.UtcNow);
        _v2PlanStore.Setup(s => s.TryGet(session.Id, out v2Record)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(6, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);
    }

    /// <summary>
    /// Design doc §3: <c>FallbackReason</c> must reflect exactly the typed reason the resolver
    /// itself recorded for THIS call, and <c>ServedBy</c> must fall back to the legacy sentinel -
    /// never a stale engine version left over from an earlier <c>POST</c>/<c>PUT</c>.
    /// </summary>
    [Theory]
    [InlineData(PlaybackLiveFallbackReason.KillSwitch)]
    [InlineData(PlaybackLiveFallbackReason.SourceIdMismatch)]
    [InlineData(PlaybackLiveFallbackReason.DolbyVisionExclusion)]
    [InlineData(PlaybackLiveFallbackReason.NoAuthoritativeRecord)]
    [InlineData(PlaybackLiveFallbackReason.PlanNotExecutable)]
    [InlineData(PlaybackLiveFallbackReason.AdapterError)]
    [InlineData(PlaybackLiveFallbackReason.StopThresholdTripped)]
    public void GetPlaybackSessionStream_Fallback_ReflectsTypedReasonAndLegacySentinel(PlaybackLiveFallbackReason reason)
    {
        var session = BuildStreamableSession(_userId, "play-session-1");
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        _liveStreamResolver
            .Setup(r => r.Resolve(It.IsAny<PlaybackSessionId>(), It.IsAny<StreamInfo>(), It.IsAny<MediaSourceInfo>(), It.IsAny<DeviceProfile>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()))
            .Returns(session.Plan.StreamInfo!);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Fallback(reason, DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSessionStream(session.Id);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, descriptor.ServedBy);
        Assert.Equal(reason, descriptor.FallbackReason);
        // Even when a v2 record happens to still be retained (a mismatch/kill-switch/etc. fallback
        // leaves it in place, per design doc §3.1), ServedBy must not report its engine version -
        // only a live-wiring outcome that actually says "served by v2" may do that.
        _v2PlanStore.Verify(s => s.TryGet(It.IsAny<PlaybackSessionId>(), out It.Ref<V2PlanRecord?>.IsAny), Times.Never);
    }

    /// <summary>
    /// Design doc §1.3/§3.2: the URL this endpoint returns for a legacy-fallback session must be
    /// exactly <c>StreamInfo.ToUrl(...)</c>'s own output for the session's planned <c>StreamInfo</c> -
    /// the same call <c>MediaInfoHelper.SetDeviceSpecificData</c> already makes to build
    /// <c>mediaSource.TranscodingUrl</c> for the identical <see cref="StreamInfo"/> shape, proving
    /// this endpoint reuses <c>ResolveServedStreamInfo</c>'s extracted logic verbatim rather than a
    /// new URL-serialization path. Uses the REAL <see cref="PlaybackLiveStreamResolver"/> (kill
    /// switch forced off), not a mock, so the fallback decision is genuinely exercised.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_LegacyFallback_UrlMatchesStreamInfoToUrl_Parity()
    {
        var mediaSource = new MediaSourceInfo { Id = "source-1", Container = "mkv", SupportsDirectPlay = true };
        var streamInfo = new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = mediaSource,
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            AudioStreamIndex = 1,
        };
        var session = BuildStreamableSession(_userId, "play-session-1", streamInfo);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var configManager = new Mock<IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(new ServerConfiguration { PlaybackShadow = new PlaybackShadowOptions { Mode = PlaybackEngineMode.Legacy } });
        var liveWiringStore = new InMemoryPlaybackLiveWiringDiagnosticsStore();
        var realResolver = new PlaybackLiveStreamResolver(
            configManager.Object,
            Mock.Of<IPlaybackExecutionPlanResolver>(),
            liveWiringStore,
            new PlaybackOperationalMetrics(),
            new PlaybackStopThresholdGuard(() => new PlaybackShadowOptions(), new PlaybackOperationalMetrics(), Mock.Of<ILogger<PlaybackStopThresholdGuard>>()),
            Mock.Of<ILogger<PlaybackLiveStreamResolver>>());

        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            _v2PlanStore.Object,
            realResolver,
            liveWiringStore,
            _mediaEncoder.Object);
        SetIdentity(controller, _userId, token: "caller-token");

        var result = controller.GetPlaybackSessionStream(session.Id, startTimeTicks: 500);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(PlaybackSessionResponse.LegacyDecisionVersion, descriptor.ServedBy);
        Assert.Equal(PlaybackLiveFallbackReason.KillSwitch, descriptor.FallbackReason);

        // Independent oracle: the exact StreamInfo.ToUrl call the legacy path itself makes.
        streamInfo.PlaySessionId = "play-session-1";
        streamInfo.StartPositionTicks = 500;
        var expectedUrl = streamInfo.ToUrl(null, "caller-token", null);
        Assert.Equal(expectedUrl, descriptor.Url);
    }

    /// <summary>
    /// Design doc §1.3/§3.2, v2-served case: the URL must exactly match
    /// <c>PlaybackExecutionPlanAdapter.ToStreamInfo</c>'s own output for the retained v2 plan - the
    /// same adapter call <c>ResolveServedStreamInfo</c>'s extracted logic makes internally. Uses the
    /// REAL <see cref="PlaybackLiveStreamResolver"/>/<see cref="PlaybackExecutionPlanResolver"/>/
    /// <see cref="InMemoryV2PlanStore"/>, not mocks, so the v2-serving decision is genuinely
    /// exercised end to end.
    /// </summary>
    [Fact]
    public void GetPlaybackSessionStream_V2Served_UrlMatchesPlaybackExecutionPlanAdapterOutput_Parity()
    {
        var mediaSource = new MediaSourceInfo
        {
            Id = "source-1",
            Container = "mkv",
            SupportsDirectPlay = true,
            MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" },
            },
        };
        var legacyStreamInfo = new StreamInfo
        {
            ItemId = _itemId,
            MediaSource = mediaSource,
            DeviceProfile = new DeviceProfile(),
            PlayMethod = PlayMethod.DirectPlay,
            Container = "mkv",
            AudioStreamIndex = 5,
            VideoCodecs = ["h264"],
            AudioCodecs = ["aac"],
        };
        var profile = new DeviceProfile();
        var options = new MediaOptions { ItemId = _itemId, UserId = _userId, DeviceId = "device-1", Profile = profile };
        var session = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            "play-session-1",
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.DirectPlay, default, legacyStreamInfo),
            default,
            default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);

        var selectedStreams = new SelectedStreams(Video: 0, Audio: 1, Subtitle: null);
        var output = new OutputSpec("mkv", "h264", "aac", null, null, null, null, null, null, StreamingProtocol.Http, null);
        var reasoning = ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method());
        var decision = PlaybackDecision.DirectPlay("source-1", selectedStreams, output, reasoning, engineVersion: 6);
        var executionPlan = new PlaybackExecutionPlan(
            PlaybackMethod.DirectPlay,
            "source-1",
            output.Container!,
            output.Protocol,
            selectedStreams.Video,
            output.VideoCodec,
            output.VideoBitrate,
            output.Resolution,
            output.VideoRange,
            selectedStreams.Audio,
            output.AudioCodec,
            output.AudioBitrate,
            output.AudioChannels,
            output.TotalBitrate,
            selectedStreams.Subtitle?.Index,
            selectedStreams.Subtitle?.Delivery,
            output.SubtitleFormat,
            new List<TransformKind>());
        var v2PlanStore = new InMemoryV2PlanStore();
        v2PlanStore.Attach(session.Id, new V2PlanRecord(decision, executionPlan, DateTimeOffset.UtcNow));

        var configManager = new Mock<IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(new ServerConfiguration { PlaybackShadow = new PlaybackShadowOptions { Mode = PlaybackEngineMode.Canary, CanaryPercentage = 100 } });
        var liveWiringStore = new InMemoryPlaybackLiveWiringDiagnosticsStore();
        var executionPlanResolver = new PlaybackExecutionPlanResolver(v2PlanStore);
        var realResolver = new PlaybackLiveStreamResolver(
            configManager.Object,
            executionPlanResolver,
            liveWiringStore,
            new PlaybackOperationalMetrics(),
            new PlaybackStopThresholdGuard(() => new PlaybackShadowOptions(), new PlaybackOperationalMetrics(), Mock.Of<ILogger<PlaybackStopThresholdGuard>>()),
            Mock.Of<ILogger<PlaybackLiveStreamResolver>>());

        var controller = new PlaybackSessionsController(
            _playbackSessionManager.Object,
            _itemLookupService.Object,
            _userManager.Object,
            _mediaSourceManager.Object,
            v2PlanStore,
            realResolver,
            liveWiringStore,
            _mediaEncoder.Object);
        SetIdentity(controller, _userId, token: "caller-token");

        var result = controller.GetPlaybackSessionStream(session.Id, startTimeTicks: 250);

        var descriptor = Assert.IsType<PlaybackSessionStreamDescriptor>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(6, descriptor.ServedBy);
        Assert.Null(descriptor.FallbackReason);

        var context = new PlaybackExecutionContext(_itemId, "play-session-1", "device-1", profile.Id?.ToString("N"), 250, false);
        var expectedStreamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(executionPlan, context, mediaSource, profile);
        expectedStreamInfo.PlaySessionId = "play-session-1";
        expectedStreamInfo.StartPositionTicks = 250;
        Assert.Equal(expectedStreamInfo.ToUrl(null, "caller-token", null), descriptor.Url);
    }
}
