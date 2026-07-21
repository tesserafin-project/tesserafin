using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// PR115d: exercises <see cref="PlaybackSessionManager"/>'s wiring of
/// <see cref="ITranscodeManager.TranscodingJobStarted"/>/<see cref="ITranscodeManager.TranscodingJobEnded"/>
/// into <see cref="PlaybackOperationalMetrics"/>' v2 transcode-start counters - see
/// <see cref="PlaybackOperationalMetrics"/>'s remarks for exactly what "failed to start" means here.
/// </summary>
public class PlaybackSessionManagerTranscodeMetricsTests
{
    private const string PlaySessionId = "play-session-1";

    [Fact]
    public void StartedThenEnded_ForV2ServedSession_RecordsSuccessfulTranscodeStart()
    {
        var (manager, mockTranscodeManager, metrics, _) = BuildFixture(servedByV2: true);

        Raise(mockTranscodeManager, started: true, ended: true);

        Assert.Equal(1, metrics.TranscodeStartAttemptsV2);
        Assert.Equal(0, metrics.TranscodeStartFailuresV2);
    }

    [Fact]
    public void EndedWithoutPriorStarted_ForV2ServedSession_RecordsFailedTranscodeStart()
    {
        var (manager, mockTranscodeManager, metrics, _) = BuildFixture(servedByV2: true);

        Raise(mockTranscodeManager, started: false, ended: true);

        Assert.Equal(1, metrics.TranscodeStartAttemptsV2);
        Assert.Equal(1, metrics.TranscodeStartFailuresV2);
    }

    [Fact]
    public void StartedThenEnded_ForLegacyServedSession_RecordsNothing()
    {
        var (manager, mockTranscodeManager, metrics, _) = BuildFixture(servedByV2: false);

        Raise(mockTranscodeManager, started: true, ended: true);

        Assert.Equal(0, metrics.TranscodeStartAttemptsV2);
        Assert.Equal(0, metrics.TranscodeStartFailuresV2);
    }

    [Fact]
    public void StartedThenEnded_NoLiveWiringOutcomeRetained_RecordsNothing()
    {
        var (manager, mockTranscodeManager, metrics, _) = BuildFixture(servedByV2: null);

        Raise(mockTranscodeManager, started: true, ended: true);

        Assert.Equal(0, metrics.TranscodeStartAttemptsV2);
    }

    [Fact]
    public void Started_ThenSessionEvictedByPlaybackStopped_ThenEnded_StillRecordsSuccessAndDoesNotLeak()
    {
        // PR115d regression: the common "user stops playback" ordering is Started -> the session's
        // OWN PlaybackStopped signal evicts the tracked session (a path unrelated to this
        // transcoding job) -> only THEN does ffmpeg's process actually exit and raise Ended. A naive
        // "check at Ended whether Started fired" design would find no correlatable session at Ended
        // time (already evicted) and silently drop what was in fact a SUCCESSFUL start - biasing the
        // observed rate toward failure. The fix records the success immediately at Started, while the
        // session is still guaranteed tracked, so this ordering must not lose it.
        var liveWiringStore = new InMemoryPlaybackLiveWiringDiagnosticsStore();
        var metrics = new PlaybackOperationalMetrics();
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var mockSessionManager = new Mock<ISessionManager>();
        var manager = new PlaybackSessionManager(
            new Mock<IPlaybackSessionPlanner>().Object,
            mockTranscodeManager.Object,
            mockSessionManager.Object,
            diagnosticsStore: null,
            v2PlanStore: null,
            liveWiringDiagnosticsStore: liveWiringStore,
            operationalMetrics: metrics);

        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), PlaySessionId);
        liveWiringStore.Record(session.Id, PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow));

        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = PlaySessionId };
        mockTranscodeManager.Raise(m => m.TranscodingJobStarted += null, mockTranscodeManager.Object, job);

        // The session's own stop signal fires and evicts it - BEFORE ffmpeg's Ended event arrives.
        var stopArgs = new PlaybackStopEventArgs { PlaySessionId = PlaySessionId };
        mockSessionManager.Raise(m => m.PlaybackStopped += null, mockSessionManager.Object, stopArgs);
        Assert.Null(manager.Get(session.Id));

        mockTranscodeManager.Raise(m => m.TranscodingJobEnded += null, mockTranscodeManager.Object, job);

        Assert.Equal(1, metrics.TranscodeStartAttemptsV2);
        Assert.Equal(0, metrics.TranscodeStartFailuresV2);
    }

    [Fact]
    public void Ended_RaisedTwiceForSameJob_RecordsOnlyOnce()
    {
        // ITranscodeManager.TranscodingJobEnded's own contract: "may be raised more than once for
        // the same job (a kill also triggers the process-exit path)". The second Ended call must not
        // double-count - see RecordTranscodeStartOutcome's remarks for why the session eviction that
        // immediately follows the first call makes this idempotent for free.
        var (manager, mockTranscodeManager, metrics, job) = BuildFixture(servedByV2: true);

        mockTranscodeManager.Raise(m => m.TranscodingJobStarted += null, mockTranscodeManager.Object, job);
        mockTranscodeManager.Raise(m => m.TranscodingJobEnded += null, mockTranscodeManager.Object, job);
        mockTranscodeManager.Raise(m => m.TranscodingJobEnded += null, mockTranscodeManager.Object, job);

        Assert.Equal(1, metrics.TranscodeStartAttemptsV2);
        Assert.Equal(0, metrics.TranscodeStartFailuresV2);
    }

    private static (PlaybackSessionManager Manager, Mock<ITranscodeManager> MockTranscodeManager, PlaybackOperationalMetrics Metrics, TranscodingJob Job) BuildFixture(bool? servedByV2)
    {
        var liveWiringStore = new InMemoryPlaybackLiveWiringDiagnosticsStore();
        var metrics = new PlaybackOperationalMetrics();
        var mockTranscodeManager = new Mock<ITranscodeManager>();
        var manager = new PlaybackSessionManager(
            new Mock<IPlaybackSessionPlanner>().Object,
            mockTranscodeManager.Object,
            new Mock<ISessionManager>().Object,
            diagnosticsStore: null,
            v2PlanStore: null,
            liveWiringDiagnosticsStore: liveWiringStore,
            operationalMetrics: metrics);

        var session = manager.Track(PlaybackMediaKind.Video, new PlaybackPlan(PlayMethod.Transcode, default), PlaySessionId);

        if (servedByV2 is bool served)
        {
            var outcome = served
                ? PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow)
                : PlaybackLiveWiringOutcome.Fallback(PlaybackLiveFallbackReason.KillSwitch, DateTimeOffset.UtcNow);
            liveWiringStore.Record(session.Id, outcome);
        }

        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = PlaySessionId };
        return (manager, mockTranscodeManager, metrics, job);
    }

    private static void Raise(Mock<ITranscodeManager> mockTranscodeManager, bool started, bool ended)
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = PlaySessionId };
        if (started)
        {
            mockTranscodeManager.Raise(m => m.TranscodingJobStarted += null, mockTranscodeManager.Object, job);
        }

        if (ended)
        {
            mockTranscodeManager.Raise(m => m.TranscodingJobEnded += null, mockTranscodeManager.Object, job);
        }
    }
}
