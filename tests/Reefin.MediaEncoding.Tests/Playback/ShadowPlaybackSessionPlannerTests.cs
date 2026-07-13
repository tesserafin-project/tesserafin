using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.MediaEncoding;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Dlna;
using Reefin.Model.Session;
using Reefin.Playback.Decision;
using Reefin.Playback.Engine;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Playback;

/// <summary>
/// Shadow-safety tests for <see cref="ShadowPlaybackSessionPlanner"/> (PR98): the v2 engine runs
/// only in shadow, so nothing it does - including throwing - may affect the plan returned to
/// callers. Legacy stays the source of truth.
/// </summary>
public class ShadowPlaybackSessionPlannerTests
{
    [Fact]
    public void PlanVideo_ShadowEngineThrows_ReturnsInnerPlanUnchangedAndDoesNotThrow()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Reefin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws<InvalidOperationException>();

        var decorator = new ShadowPlaybackSessionPlanner(mockInner.Object, mockEngine.Object, NullLogger<ShadowPlaybackSessionPlanner>.Instance);

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
    }

    [Fact]
    public void PlanAudio_ShadowEngineThrows_ReturnsInnerPlanUnchangedAndDoesNotThrow()
    {
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanAudio(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Reefin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws<InvalidOperationException>();

        var decorator = new ShadowPlaybackSessionPlanner(mockInner.Object, mockEngine.Object, NullLogger<ShadowPlaybackSessionPlanner>.Instance);

        var result = decorator.PlanAudio(options);

        Assert.Same(expectedPlan, result);
    }

    [Fact]
    public void PlanVideo_InnerReturnsNull_ReturnsNullAndDoesNotThrow()
    {
        // The "no viable plan" case: options with empty MediaSources is exactly the shape that
        // previously produced a v2 NRE (per PR98 spec) - the shadow path must contain it.
        var options = CreateOptions();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns((PlaybackPlan?)null);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Reefin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Throws<NullReferenceException>();

        var decorator = new ShadowPlaybackSessionPlanner(mockInner.Object, mockEngine.Object, NullLogger<ShadowPlaybackSessionPlanner>.Instance);

        var result = decorator.PlanVideo(options);

        Assert.Null(result);
    }

    [Fact]
    public void PlanVideo_ShadowEngineSucceeds_StillReturnsInnerPlanUnchangedByReference()
    {
        // Even when the shadow run succeeds cleanly, its decision must never be substituted for the
        // legacy plan: legacy is the source of truth, unconditionally.
        var options = CreateOptions();
        var expectedPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(expectedPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Reefin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                SelectedStreams.None,
                OutputSpec.Empty,
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));

        var decorator = new ShadowPlaybackSessionPlanner(mockInner.Object, mockEngine.Object, NullLogger<ShadowPlaybackSessionPlanner>.Instance);

        var result = decorator.PlanVideo(options);

        Assert.Same(expectedPlan, result);
    }

    private static MediaOptions CreateOptions() => new() { Profile = new DeviceProfile() };
}
