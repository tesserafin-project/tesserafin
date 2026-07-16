using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Controller.Session;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;
using Reefin.Model.Session;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Playback;

/// <summary>
/// Lifecycle tests for <see cref="PlaybackSessionManager"/>: create/patch/delete bookkeeping
/// on top of an <see cref="IPlaybackSessionPlanner"/> stub.
/// </summary>
public class PlaybackSessionManagerTests
{
    [Fact]
    public void Create_ViablePlan_StoresAndReturnsSession()
    {
        var options = CreateOptions();
        var plan = CreatePlan();
        var manager = GetManager(planner => planner.Setup(p => p.PlanVideo(options)).Returns(plan));

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.Same(plan, session.Plan);
        Assert.Equal(session, manager.Get(session.Id));
    }

    [Fact]
    public void Create_NoViablePlan_ReturnsNullAndDoesNotStore()
    {
        var options = CreateOptions();
        var manager = GetManager(planner => planner.Setup(p => p.PlanAudio(options)).Returns((PlaybackPlan?)null));

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Audio, options));

        Assert.Null(session);
    }

    [Fact]
    public void Patch_ExistingSession_ReplacesPlanAndKeepsId()
    {
        var initialOptions = CreateOptions();
        var patchedOptions = CreateOptions();
        var initialPlan = CreatePlan();
        var patchedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        mockPlanner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockPlanner.Setup(p => p.PlanVideo(patchedOptions)).Returns(patchedPlan);
        var manager = new PlaybackSessionManager(mockPlanner.Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions));
        Assert.NotNull(session);

        var patched = manager.Patch(session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, patchedOptions));

        Assert.NotNull(patched);
        Assert.Equal(session.Id, patched.Id);
        Assert.Same(patchedPlan, patched.Plan);
        Assert.Same(patchedPlan, manager.Get(session.Id)?.Plan);
    }

    [Fact]
    public void Patch_UnknownSession_ReturnsNull()
    {
        var manager = GetManager(_ => { });

        var patched = manager.Patch(PlaybackSessionId.NewId(), new PlaybackSessionRequest(PlaybackMediaKind.Video, CreateOptions()));

        Assert.Null(patched);
    }

    [Fact]
    public void Delete_ExistingSession_RemovesIt()
    {
        var options = CreateOptions();
        var plan = CreatePlan();
        var manager = GetManager(planner => planner.Setup(p => p.PlanVideo(options)).Returns(plan));
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));
        Assert.NotNull(session);

        var deleted = manager.Delete(session.Id);

        Assert.True(deleted);
        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void Delete_UnknownSession_ReturnsFalse()
    {
        var manager = GetManager(_ => { });

        Assert.False(manager.Delete(PlaybackSessionId.NewId()));
    }

    [Fact]
    public void Track_StoresSessionWithoutCallingPlanner()
    {
        var manager = GetManager(_ => { });
        var plan = new PlaybackPlan(PlayMethod.DirectStream, default);

        var session = manager.Track(PlaybackMediaKind.Video, plan);

        Assert.Equal(PlaybackMediaKind.Video, session.Kind);
        Assert.Null(session.Request);
        Assert.Same(plan, session.Plan);
        Assert.Equal(session, manager.Get(session.Id));
    }

    [Fact]
    public void Track_SamePlaySessionIdTwice_ReplacesInsteadOfAdding()
    {
        var manager = GetManager(_ => { });
        var initialPlan = new PlaybackPlan(PlayMethod.DirectStream, default);
        var updatedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);

        var first = manager.Track(PlaybackMediaKind.Video, initialPlan, "play-session-1");
        var second = manager.Track(PlaybackMediaKind.Video, updatedPlan, "play-session-1");

        Assert.Equal(first.Id, second.Id);
        Assert.Same(updatedPlan, manager.Get(first.Id)?.Plan);
    }

    [Fact]
    public void DeleteByPlaySessionId_RemovesTrackedSession()
    {
        var manager = GetManager(_ => { });
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        Assert.True(manager.DeleteByPlaySessionId("play-session-1"));
        Assert.Null(manager.Get(session.Id));
        Assert.False(manager.DeleteByPlaySessionId("play-session-1"));
    }

    [Fact]
    public void TranscodingJobEnded_RemovesSessionWithMatchingPlaySessionId()
    {
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, mockTranscodeManager.Object, new Mock<ISessionManager>().Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = "play-session-1" };
        mockTranscodeManager.Raise(m => m.TranscodingJobEnded += null, mockTranscodeManager.Object, job);

        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void Create_SamePlaySessionIdTwice_ReplacesInsteadOfAdding()
    {
        var initialOptions = CreateOptions();
        var patchedOptions = CreateOptions();
        var initialPlan = CreatePlan();
        var patchedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        mockPlanner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockPlanner.Setup(p => p.PlanVideo(patchedOptions)).Returns(patchedPlan);
        var manager = new PlaybackSessionManager(mockPlanner.Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);

        var first = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions), "play-session-1");
        var second = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, patchedOptions), "play-session-1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Same(patchedPlan, manager.Get(first.Id)?.Plan);
    }

    [Fact]
    public void PlaybackStopped_RemovesSessionWithMatchingPlaySessionId()
    {
        var mockSessionManager = new Mock<ISessionManager>();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, mockSessionManager.Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var args = new PlaybackStopEventArgs { PlaySessionId = "play-session-1" };
        mockSessionManager.Raise(m => m.PlaybackStopped += null, mockSessionManager.Object, args);

        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void SweepExpired_PastTtl_RemovesSession()
    {
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var removed = manager.SweepExpired(DateTimeOffset.UtcNow.AddHours(7));

        Assert.Equal(1, removed);
        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void SweepExpired_WithinTtl_KeepsSession()
    {
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var removed = manager.SweepExpired(DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal(0, removed);
        Assert.NotNull(manager.Get(session.Id));
    }

    [Fact]
    public void TranscodingJobStarted_RecordsFfmpegStartedEventForMatchingSession()
    {
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, mockTranscodeManager.Object, new Mock<ISessionManager>().Object, store);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = "play-session-1" };
        mockTranscodeManager.Raise(m => m.TranscodingJobStarted += null, mockTranscodeManager.Object, job);

        var recorded = Assert.Single(store.GetEvents(session.Id));
        Assert.Equal("FfmpegStarted", recorded.Stage);
    }

    [Fact]
    public void TranscodingJobStarted_UnknownPlaySessionId_RecordsNothing()
    {
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, mockTranscodeManager.Object, new Mock<ISessionManager>().Object, store);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-1");

        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = "some-other-play-session" };
        mockTranscodeManager.Raise(m => m.TranscodingJobStarted += null, mockTranscodeManager.Object, job);

        Assert.Empty(store.GetEvents(session.Id));
    }

    [Fact]
    public void PlaybackStart_RecordsPlaybackStartedEventForMatchingSession()
    {
        var mockSessionManager = new Mock<ISessionManager>();
        var store = new InMemoryShadowDiagnosticsStore();
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, mockSessionManager.Object, store);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var args = new PlaybackProgressEventArgs { PlaySessionId = "play-session-1" };
        mockSessionManager.Raise(m => m.PlaybackStart += null, mockSessionManager.Object, args);

        var recorded = Assert.Single(store.GetEvents(session.Id));
        Assert.Equal("PlaybackStarted", recorded.Stage);
    }

    /// <summary>
    /// PR113b: <c>PlaybackStopped</c> records its event before evicting the session - but
    /// <c>RemoveNoLock</c> (reached via <c>DeleteByPlaySessionId</c>, called synchronously right
    /// after) evicts every retained event for the session along with it, so
    /// <see cref="IShadowDiagnosticsStore.GetEvents"/> is empty again by the time this handler
    /// returns. A store spy proves the ordering (<c>RecordEvent</c> then <c>Remove</c>) that a
    /// plain end-to-end assertion on <c>GetEvents</c> after the fact could never distinguish from
    /// "never recorded at all".
    /// </summary>
    [Fact]
    public void PlaybackStopped_RecordsEventBeforeSessionRemovalEvictsIt()
    {
        var mockSessionManager = new Mock<ISessionManager>();
        var mockStore = new Mock<IShadowDiagnosticsStore>();
        var calls = new List<string>();
        mockStore
            .Setup(s => s.RecordEvent(It.IsAny<PlaybackSessionId>(), It.IsAny<PlaybackLifecycleEvent>()))
            .Callback<PlaybackSessionId, PlaybackLifecycleEvent>((_, lifecycleEvent) => calls.Add($"RecordEvent:{lifecycleEvent.Stage}"));
        mockStore
            .Setup(s => s.Remove(It.IsAny<PlaybackSessionId>()))
            .Callback<PlaybackSessionId>(_ => calls.Add("Remove"));
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, mockSessionManager.Object, mockStore.Object);
        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");

        var args = new PlaybackStopEventArgs { PlaySessionId = "play-session-1" };
        mockSessionManager.Raise(m => m.PlaybackStopped += null, mockSessionManager.Object, args);

        Assert.Equal(new[] { "RecordEvent:PlaybackStopped", "Remove" }, calls);
        Assert.Null(manager.Get(session.Id));
    }

    [Fact]
    public void GetAll_ReturnsSnapshotOfAllTrackedSessions()
    {
        var manager = new PlaybackSessionManager(new Mock<IPlaybackSessionPlanner>().Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
        var first = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.DirectPlay, default), "play-session-1");
        var second = manager.Track(PlaybackMediaKind.Audio, new PlaybackPlan(PlayMethod.Transcode, default), "play-session-2");

        var all = manager.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Id == first.Id);
        Assert.Contains(all, s => s.Id == second.Id);
    }

    private static PlaybackSessionManager GetManager(System.Action<Mock<IPlaybackSessionPlanner>> setup)
    {
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        setup(mockPlanner);
        return new PlaybackSessionManager(mockPlanner.Object, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object);
    }

    private static MediaOptions CreateOptions() => new() { Profile = new DeviceProfile() };

    private static PlaybackPlan CreatePlan() => new(PlayMethod.DirectPlay, default);
}
