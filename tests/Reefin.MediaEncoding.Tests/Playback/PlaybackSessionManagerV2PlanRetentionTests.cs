using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Controller.Session;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Configuration;
using Reefin.Model.Dlna;
using Reefin.Model.Session;
using Reefin.Playback.Decision;
using Reefin.Playback.Engine;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Playback;

/// <summary>
/// End-to-end retention tests for PR115a: wires the real <see cref="ShadowPlaybackSessionPlanner"/>
/// (mock inner planner + mock v2 engine) into a real <see cref="PlaybackSessionManager"/>, both
/// sharing one <see cref="InMemoryV2PlanStore"/> instance - exactly the DI shape <c>ApplicationHost</c>
/// wires. Mirrors <see cref="PlaybackSessionManagerDiagnosticsRetentionTests"/>'s shape, but for the
/// AUTHORITATIVE v2 channel rather than the observability one: proves
/// <see cref="ShadowPlaybackSessionPlanner.RunShadow"/> actually publishes a <see cref="V2PlanRecord"/>
/// into the ambient slot only when the effective mode makes it authoritative, and that
/// <see cref="PlaybackSessionManager"/> correlates it onto the real, freshly-minted session id -
/// attaching, evicting, or leaving untouched exactly as the retained diagnostics record does.
/// </summary>
public class PlaybackSessionManagerV2PlanRetentionTests
{
    [Fact]
    public void Create_ModeV2_RetainsRecordWithNonNullExecutionPlanKeyedByRealSessionId()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var v2Store = new InMemoryV2PlanStore();
        var manager = BuildManager(options, legacyPlan, v2Store, () => PlaybackEngineMode.V2);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.True(v2Store.TryGet(session!.Id, out var record));
        Assert.NotNull(record);
        Assert.NotNull(record!.ExecutionPlan);
        Assert.True(record.Decision.IsViable);
    }

    [Fact]
    public void Create_ModeShadow_RetainsNoRecord()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var v2Store = new InMemoryV2PlanStore();
        var manager = BuildManager(options, legacyPlan, v2Store, () => PlaybackEngineMode.Shadow);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));

        Assert.NotNull(session);
        Assert.False(v2Store.TryGet(session!.Id, out var record));
        Assert.Null(record);
    }

    [Fact]
    public void Patch_ConfigFlippedToShadow_EvictsPreviouslyAttachedV2Record()
    {
        var initialOptions = CreateOptions();
        var patchedOptions = CreateOptions();
        var initialPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var patchedPlan = new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported);
        var v2Store = new InMemoryV2PlanStore();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(initialOptions)).Returns(initialPlan);
        mockInner.Setup(p => p.PlanVideo(patchedOptions)).Returns(patchedPlan);
        var mockEngine = BuildEngineMock();

        // Mutable mode captured by the accessor closure - Create runs with V2 authoritative, then
        // config flips to Shadow before the Patch, mirroring a mid-rollout mode change.
        var mode = PlaybackEngineMode.V2;
        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = mode, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);
        var manager = new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, diagnosticsStore: null, v2PlanStore: v2Store);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, initialOptions));
        Assert.NotNull(session);
        Assert.True(v2Store.TryGet(session!.Id, out _));

        mode = PlaybackEngineMode.Shadow;
        var patched = manager.Patch(session.Id, new PlaybackSessionRequest(PlaybackMediaKind.Video, patchedOptions));

        Assert.NotNull(patched);
        Assert.False(v2Store.TryGet(patched!.Id, out var record));
        Assert.Null(record);
    }

    [Fact]
    public void Delete_ExistingSessionWithRetainedV2Record_EvictsRecord()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var v2Store = new InMemoryV2PlanStore();
        var manager = BuildManager(options, legacyPlan, v2Store, () => PlaybackEngineMode.V2);
        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));
        Assert.NotNull(session);
        Assert.True(v2Store.TryGet(session!.Id, out _));

        var deleted = manager.Delete(session.Id);

        Assert.True(deleted);
        Assert.False(v2Store.TryGet(session.Id, out var record));
        Assert.Null(record);
    }

    private static PlaybackSessionManager BuildManager(
        MediaOptions options,
        PlaybackPlan legacyPlan,
        InMemoryV2PlanStore v2Store,
        Func<PlaybackEngineMode> modeAccessor)
    {
        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(legacyPlan);
        var mockEngine = BuildEngineMock();

        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Mode = modeAccessor(), SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: null,
            v2PlanStore: v2Store);

        return new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, diagnosticsStore: null, v2PlanStore: v2Store);
    }

    private static Mock<IPlaybackEngine> BuildEngineMock()
    {
        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<Reefin.Playback.Decision.ClientCapabilities>(), It.IsAny<System.Collections.Generic.IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(PlaybackDecision.DirectPlay(
                "source-1",
                new SelectedStreams(Video: 0, Audio: 1, Subtitle: null),
                new OutputSpec(
                    Container: "mp4",
                    VideoCodec: "h264",
                    AudioCodec: "aac",
                    Resolution: null,
                    VideoRange: null,
                    AudioChannels: null,
                    TotalBitrate: null,
                    VideoBitrate: null,
                    AudioBitrate: null,
                    Protocol: StreamingProtocol.Http,
                    SubtitleFormat: null),
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: PlaybackEngine.EngineVersion));
        return mockEngine;
    }

    private static MediaOptions CreateOptions() => new() { Profile = new DeviceProfile() };
}
