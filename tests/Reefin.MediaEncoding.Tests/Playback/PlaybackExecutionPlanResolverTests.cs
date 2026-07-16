using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Controller.Session;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Configuration;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Session;
using Reefin.Playback.Decision;
using Reefin.Playback.Dlna;
using Reefin.Playback.Engine;
using Reefin.Playback.Execution;
using Reefin.Playback.Shadow;
using Xunit;
using DomainClientCapabilities = Reefin.Playback.Decision.ClientCapabilities;

namespace Reefin.MediaEncoding.Tests.Playback;

/// <summary>
/// Tests for <see cref="PlaybackExecutionPlanResolver"/> (PR114a): unit-level refusal/absence cases,
/// plus an end-to-end test wiring the real <see cref="PlaybackSessionManager"/>/
/// <see cref="ShadowPlaybackSessionPlanner"/>/<see cref="InMemoryShadowDiagnosticsStore"/> (same shape
/// as <see cref="PlaybackSessionManagerDiagnosticsRetentionTests"/>) that creates a session with shadow
/// active, resolves its plan by session id, and converts it to a legacy <see cref="StreamInfo"/> via
/// <see cref="PlaybackExecutionPlanAdapter"/> - proving the whole
/// <c>PlaybackDecision -&gt; PlaybackExecutionPlan -&gt; StreamInfo</c> contract this PR builds, even
/// though nothing on the live streaming path consumes it yet.
/// </summary>
public class PlaybackExecutionPlanResolverTests
{
    [Fact]
    public void Resolve_UnknownSessionId_ReturnsNull()
    {
        var store = new InMemoryShadowDiagnosticsStore();
        var resolver = new PlaybackExecutionPlanResolver(store);

        var plan = resolver.Resolve(PlaybackSessionId.NewId());

        Assert.Null(plan);
    }

    [Fact]
    public void Resolve_RecordHoldsNotViableDecision_ReturnsNull()
    {
        var store = new InMemoryShadowDiagnosticsStore();
        var id = PlaybackSessionId.NewId();
        var notViable = PlaybackDecision.NotViable(
            PlaybackMethod.Transcode,
            new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, []),
            engineVersion: PlaybackEngine.EngineVersion);
        store.Attach(id, BuildRecord(notViable));
        var resolver = new PlaybackExecutionPlanResolver(store);

        var plan = resolver.Resolve(id);

        Assert.Null(plan);
    }

    [Fact]
    public void Resolve_RecordHoldsViableDecision_ReturnsBuiltPlan()
    {
        var store = new InMemoryShadowDiagnosticsStore();
        var id = PlaybackSessionId.NewId();
        store.Attach(id, BuildRecord(BuildViableDirectPlayDecision()));
        var resolver = new PlaybackExecutionPlanResolver(store);

        var plan = resolver.Resolve(id);

        Assert.NotNull(plan);
        Assert.Equal(PlaybackMethod.DirectPlay, plan!.Method);
        Assert.Equal("source-1", plan.SourceId);
    }

    /// <summary>
    /// End-to-end: real session creation with shadow active resolves to a plan that converts into a
    /// coherent legacy <see cref="StreamInfo"/> - the source, streams, and codecs v2 selected are
    /// preserved unchanged all the way through.
    /// </summary>
    [Fact]
    public void CreateSessionWithShadowActive_ResolveById_ConvertsToCoherentStreamInfo()
    {
        var options = CreateOptions();
        var legacyPlan = new PlaybackPlan(PlayMethod.DirectPlay, default);
        var store = new InMemoryShadowDiagnosticsStore();

        var mockInner = new Mock<IPlaybackSessionPlanner>();
        mockInner.Setup(p => p.PlanVideo(options)).Returns(legacyPlan);

        var mockEngine = new Mock<IPlaybackEngine>();
        mockEngine
            .Setup(e => e.Decide(It.IsAny<PlaybackRequestContext>(), It.IsAny<DomainClientCapabilities>(), It.IsAny<IReadOnlyList<MediaSourceSnapshot>>(), It.IsAny<PlaybackConstraints>()))
            .Returns(BuildViableDirectPlayDecision());

        var shadowPlanner = new ShadowPlaybackSessionPlanner(
            mockInner.Object,
            mockEngine.Object,
            NullLogger<ShadowPlaybackSessionPlanner>.Instance,
            () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 },
            metrics: null,
            diagnosticsStore: store);
        var manager = new PlaybackSessionManager(shadowPlanner, new Mock<ITranscodeManager>().Object, new Mock<ISessionManager>().Object, store);
        var resolver = new PlaybackExecutionPlanResolver(store);

        var session = manager.Create(new PlaybackSessionRequest(PlaybackMediaKind.Video, options));
        Assert.NotNull(session);

        var plan = resolver.Resolve(session!.Id);
        Assert.NotNull(plan);
        Assert.Equal("source-1", plan!.SourceId);

        var mediaSource = new MediaSourceInfo { Id = "source-1", Container = "mkv" };
        var deviceProfile = new DeviceProfile();
        var streamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, mediaSource, deviceProfile, itemId: Guid.NewGuid());

        Assert.Equal(PlayMethod.DirectPlay, streamInfo.PlayMethod);
        Assert.Equal("mp4", streamInfo.Container);
        Assert.Equal("source-1", streamInfo.MediaSourceId);
        Assert.Same(mediaSource, streamInfo.MediaSource);
        Assert.Equal(1, streamInfo.AudioStreamIndex);
        Assert.Equal(["h264"], streamInfo.VideoCodecs);
        Assert.Equal(["aac"], streamInfo.AudioCodecs);
    }

    private static PlaybackDecision BuildViableDirectPlayDecision() => PlaybackDecision.DirectPlay(
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
        engineVersion: PlaybackEngine.EngineVersion);

    private static ShadowDiagnosticRecord BuildRecord(PlaybackDecision decision) => new(
        decision,
        new DecisionVector(
            IsViable: decision.IsViable,
            Method: null,
            VideoStreamIndex: StreamSelection.Unknown,
            AudioStreamIndex: StreamSelection.Unknown,
            SubtitleStreamIndex: StreamSelection.Unknown,
            TransformClasses: new HashSet<TransformClass>(),
            ReasonCategories: new HashSet<ReasonCategory>(),
            OutputContainer: null,
            OutputVideoCodec: null,
            OutputAudioCodec: null,
            SelectedSource: null,
            OutputWidth: null,
            OutputHeight: null,
            OutputBitrate: null,
            OutputVideoRange: null,
            OutputAudioChannels: null,
            SubtitleDeliveryMode: null,
            OutputSubtitleFormat: null),
        new ShadowDivergence(
            DivergenceClass.Equivalent,
            MethodDiffers: false,
            StreamsDiffer: false,
            OnlyLegacy: new HashSet<TransformClass>(),
            OnlyV2: new HashSet<TransformClass>(),
            ReasonOnlyLegacy: new HashSet<ReasonCategory>(),
            ReasonOnlyV2: new HashSet<ReasonCategory>(),
            Summary: "test fixture"),
        new PlaybackRequestContext(Guid.NewGuid(), Guid.NewGuid(), null, Guid.Empty, MediaKind.Video, DateTimeOffset.UtcNow, PlaybackEngine.EngineVersion),
        new DomainClientCapabilities(new DecodeCapabilities([], [], [], [], SupportsHls: true, SupportsDash: false), []),
        [],
        new PlaybackConstraints(true, true, true, true, true, null, null, null, null, SubtitlePlaybackMode.Default, [], false, 0),
        PlaybackMediaKind.Video,
        DateTimeOffset.UtcNow);

    private static MediaOptions CreateOptions() => new() { Profile = new DeviceProfile() };
}
