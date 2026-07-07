using Moq;
using Reefin.Controller.MediaEncoding;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;
using Reefin.Model.Session;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Playback;

/// <summary>
/// Lifecycle tests for <see cref="PlaybackSessionManager"/>: create/patch/delete bookkeeping
/// on top of an <see cref="IPlaybackSessionPlanner"/> stub. Not wired into any controller yet.
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
        var manager = new PlaybackSessionManager(mockPlanner.Object);
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

    private static PlaybackSessionManager GetManager(System.Action<Mock<IPlaybackSessionPlanner>> setup)
    {
        var mockPlanner = new Mock<IPlaybackSessionPlanner>();
        setup(mockPlanner);
        return new PlaybackSessionManager(mockPlanner.Object);
    }

    private static MediaOptions CreateOptions() => new() { Profile = new DeviceProfile() };

    private static PlaybackPlan CreatePlan() => new(PlayMethod.DirectPlay, default);
}
