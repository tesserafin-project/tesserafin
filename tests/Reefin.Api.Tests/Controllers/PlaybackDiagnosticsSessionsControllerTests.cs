using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Reefin.Api.Controllers;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Api.Tests.Models.PlaybackSessionDtos;
using Reefin.Common.Api;
using Reefin.Controller.MediaEncoding;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;
using Reefin.Model.Session;
using Xunit;

namespace Reefin.Api.Tests.Controllers;

public class PlaybackDiagnosticsSessionsControllerTests
{
    private readonly Mock<IPlaybackSessionManager> _playbackSessionManager = new();
    private readonly Mock<IShadowDiagnosticsStore> _diagnosticsStore = new();

    private PlaybackDiagnosticsSessionsController CreateController()
        => new(_playbackSessionManager.Object, _diagnosticsStore.Object);

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
    public void GetPlaybackSessions_ReturnsListItemsWithHasDiagnosticFlags()
    {
        var withDiagnostic = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        var withoutDiagnostic = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Audio, null, null, new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported), default, default);
        _playbackSessionManager.Setup(m => m.GetAll()).Returns([withDiagnostic, withoutDiagnostic]);

        var record = FakeShadowDiagnosticRecordFactory.Create();
        _diagnosticsStore.Setup(s => s.TryGet(withDiagnostic.Id, out record)).Returns(true);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(withoutDiagnostic.Id, out none)).Returns(false);

        var result = CreateController().GetPlaybackSessions();

        var items = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<PlaybackSessionListItem>>(
            Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, items.Count);
        Assert.True(items.Single(i => i.Session.Id.Equals(withDiagnostic.Id.Value)).HasDiagnostic);
        Assert.False(items.Single(i => i.Session.Id.Equals(withoutDiagnostic.Id.Value)).HasDiagnostic);
    }

    /// <summary>
    /// PR114: a session tracked directly (no <see cref="PlaybackSessionRequest"/> attached, the
    /// <c>IPlaybackSessionManager.Track</c> path) must yield <see langword="null"/> list-item
    /// <c>ItemId</c>/<c>DeviceId</c> rather than throw or fabricate a value, while a session planned
    /// from a real request surfaces both raw identifiers as-is.
    /// </summary>
    [Fact]
    public void GetPlaybackSessions_PopulatesItemIdAndDeviceIdFromRequestWhenPresent()
    {
        var itemId = Guid.NewGuid();
        const string deviceId = "device-abc";
        var options = new MediaOptions { ItemId = itemId, DeviceId = deviceId, Profile = new DeviceProfile() };
        var withRequest = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            null,
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.DirectPlay, default),
            default,
            default);
        var trackedOnly = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.GetAll()).Returns([withRequest, trackedOnly]);

        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(It.IsAny<PlaybackSessionId>(), out none)).Returns(false);

        var result = CreateController().GetPlaybackSessions();

        var items = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<PlaybackSessionListItem>>(
            Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        var requestedItem = items.Single(i => i.Session.Id.Equals(withRequest.Id.Value));
        Assert.Equal(itemId, requestedItem.ItemId);
        Assert.Equal(deviceId, requestedItem.DeviceId);

        var trackedItem = items.Single(i => i.Session.Id.Equals(trackedOnly.Id.Value));
        Assert.Null(trackedItem.ItemId);
        Assert.Null(trackedItem.DeviceId);
    }

    [Fact]
    public void GetPlaybackSession_ExistingSessionWithDiagnostic_ReturnsFullDetail()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        var record = FakeShadowDiagnosticRecordFactory.Create();
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out record)).Returns(true);

        var result = CreateController().GetPlaybackSession(session.Id);

        var detail = Assert.IsAssignableFrom<PlaybackDiagnosticDetail>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(session.Id.Value, detail.Id);
        Assert.NotNull(detail.RequestContext);
        Assert.NotNull(detail.Capabilities);
        Assert.NotNull(detail.SourceSnapshot);
        Assert.NotNull(detail.Reasoning);
        Assert.NotNull(detail.Comparison);
        Assert.NotNull(detail.Comparison!.DivergenceSummary);
        Assert.Equal(record.Decision.Method, detail.Comparison.V2Method);
    }

    [Fact]
    public void GetPlaybackSession_ExistingSessionWithoutDiagnostic_ReturnsBaseOnlyDetail()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out none)).Returns(false);

        var result = CreateController().GetPlaybackSession(session.Id);

        var detail = Assert.IsAssignableFrom<PlaybackDiagnosticDetail>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(session.Id.Value, detail.Id);
        Assert.Null(detail.RequestContext);
        Assert.Null(detail.Capabilities);
        Assert.Null(detail.SourceSnapshot);
        Assert.Null(detail.Reasoning);
        Assert.Null(detail.Comparison);
    }

    [Fact]
    public void GetPlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = CreateController().GetPlaybackSession(id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// PR113b: real, observed lifecycle events retrieved from the store must reach the mapped
    /// detail's timeline, appended after the always-present <c>Created</c>/<c>Updated</c> entries.
    /// </summary>
    [Fact]
    public void GetPlaybackSession_WithRecordedEvents_TimelineIncludesThem()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out none)).Returns(false);
        var lifecycleEvent = new PlaybackLifecycleEvent("PlaybackStarted", DateTimeOffset.UnixEpoch);
        _diagnosticsStore.Setup(s => s.GetEvents(session.Id)).Returns([lifecycleEvent]);

        var result = CreateController().GetPlaybackSession(session.Id);

        var detail = Assert.IsAssignableFrom<PlaybackDiagnosticDetail>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Contains(detail.Timeline, e => e.Stage == "PlaybackStarted" && e.At == DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void ExportFixture_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = CreateController().ExportFixture(id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void ExportFixture_SessionWithoutRetainedDiagnostic_ReturnsUnprocessableEntity()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out none)).Returns(false);

        var result = CreateController().ExportFixture(session.Id);

        Assert.IsType<UnprocessableEntityResult>(result);
    }

    [Fact]
    public void ExportFixture_SessionWithRetainedDiagnostic_ReturnsCamelCaseJsonContent()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        var record = FakeShadowDiagnosticRecordFactory.Create();
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out record)).Returns(true);

        var result = Assert.IsType<ContentResult>(CreateController().ExportFixture(session.Id));

        Assert.Equal("application/json", result.ContentType);
        Assert.Contains("\"fixtureVersion\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"engineVersion\"", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FixtureVersion\"", result.Content, StringComparison.Ordinal);
    }
}
