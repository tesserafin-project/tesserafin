using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Reefin.Controller.Diagnostics;
using Reefin.Controller.Library;
using Reefin.Controller.MediaEncoding;
using Reefin.Controller.Session;
using Reefin.Model.Session;

namespace Reefin.MediaEncoding.Playback;

/// <inheritdoc cref="IPlaybackSessionManager"/>
public sealed class PlaybackSessionManager : IPlaybackSessionManager, IDisposable
{
    // Direct-play sessions have no transcoding job to signal their end, and a client can call
    // PlaybackInfo without ever starting playback. PlaybackStopped (see constructor) covers the
    // former in the common case; this TTL is the backstop for both when that signal never
    // arrives (crash, client bug, browser closed mid-probe). Generous on purpose: legitimate
    // playback can run for hours without any Create/Patch/Track call to refresh UpdatedAt.
    private static readonly TimeSpan ExpiryTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);

    private readonly IPlaybackSessionPlanner _planner;
    private readonly ITranscodeManager _transcodeManager;
    private readonly ISessionManager _sessionManager;
    private readonly IShadowDiagnosticsStore _diagnosticsStore;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly IPlaybackLiveWiringDiagnosticsStore _liveWiringDiagnosticsStore;
    private readonly PlaybackOperationalMetrics _operationalMetrics;
    private readonly IRequestCorrelationAccessor _requestCorrelation;
    private readonly object _lock = new();
    private readonly Dictionary<PlaybackSessionId, PlaybackSession> _sessions = new();
    private readonly Dictionary<string, PlaybackSessionId> _byPlaySessionId = new(StringComparer.OrdinalIgnoreCase);

    // PR115d: play session ids for which ITranscodeManager.TranscodingJobStarted has actually fired -
    // consulted (and removed) by RecordTranscodeStartFailureIfNeverStarted to distinguish "ffmpeg
    // started, then the job ended" (already recorded as a success at Started time, nothing further to
    // do) from "ffmpeg never started" (a failure, recorded here). Guarded by _lock, same as
    // _byPlaySessionId - both are read/written from the same event handlers.
    private readonly HashSet<string> _startedPlaySessionIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly Timer _sweepTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionManager"/> class.
    /// </summary>
    /// <param name="planner">Instance of the <see cref="IPlaybackSessionPlanner"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="diagnosticsStore">
    /// PR113: retains whatever shadow diagnostic <paramref name="planner"/> produced for a
    /// <see cref="Create"/>/<see cref="Patch"/> call, keyed by the resulting session id, for the
    /// admin diagnostics endpoint. Defaults to a no-op instance when not supplied, so every
    /// pre-PR113 (3-arg) call site - including existing test constructors - keeps compiling and
    /// behaving exactly as before: no diagnostic is ever retained. PR113b: also the sink for real
    /// lifecycle events (<see cref="ITranscodeManager.TranscodingJobStarted"/>,
    /// <see cref="ISessionManager.PlaybackStart"/>/<see cref="ISessionManager.PlaybackStopped"/>),
    /// which this constructor subscribes to unconditionally - correlation against a tracked session
    /// silently no-ops when the store is the no-op default, same as every other diagnostics path.
    /// </param>
    /// <param name="v2PlanStore">
    /// PR115a: retains the AUTHORITATIVE v2 plan (<see cref="V2PlanRecord"/>) <paramref name="planner"/>
    /// publishes when a <see cref="Create"/>/<see cref="Patch"/> call is served by v2, keyed by the
    /// resulting session id - a separate channel from <paramref name="diagnosticsStore"/>, which only
    /// ever holds observability data. Defaults to a no-op instance when not supplied, so every
    /// pre-PR115a call site - including existing test constructors - keeps compiling and behaving
    /// exactly as before: no authoritative record is ever retained.
    /// </param>
    /// <param name="liveWiringDiagnosticsStore">
    /// PR115c: retains the observable outcome (<see cref="PlaybackLiveWiringOutcome"/>) of the live
    /// streaming path's serve-v2-or-fallback decision - a third, separate channel from both
    /// <paramref name="diagnosticsStore"/> and <paramref name="v2PlanStore"/>. This manager never
    /// writes to it (the decision is made downstream, in <c>MediaInfoHelper</c>, after a session is
    /// already in hand) - it only evicts it, on the exact same removal paths as the other two
    /// per-session stores, so a stale outcome can never outlive the session it describes. Defaults
    /// to a no-op instance when not supplied, so every pre-PR115c call site - including existing test
    /// constructors - keeps compiling and behaving exactly as before: nothing is ever retained.
    /// </param>
    /// <param name="operationalMetrics">
    /// PR115d: where <see cref="OnTranscodingJobStarted"/>/<see cref="OnTranscodingJobEnded"/> record
    /// the ffmpeg transcode-start outcome for v2-served sessions - see
    /// <see cref="PlaybackOperationalMetrics"/>'s remarks for exactly what is recorded and why.
    /// Defaults to a fresh, unshared instance when not supplied, so every pre-PR115d call site -
    /// including existing test constructors - keeps compiling and behaving exactly as before:
    /// outcomes are recorded but never observed by anything outside this instance.
    /// </param>
    /// <param name="requestCorrelation">
    /// Issue #42: supplies the correlation id of the HTTP request in flight, stamped onto each
    /// <see cref="PlaybackLifecycleEvent"/> this manager records. Additive to the
    /// <see cref="PlaybackSessionId"/> grouping key, never a replacement for it. Defaults to
    /// <see cref="NullRequestCorrelationAccessor"/> when not supplied, so every pre-#42 call site —
    /// including existing test constructors — keeps compiling and simply records no request id.
    /// </param>
    public PlaybackSessionManager(
        IPlaybackSessionPlanner planner,
        ITranscodeManager transcodeManager,
        ISessionManager sessionManager,
        IShadowDiagnosticsStore? diagnosticsStore = null,
        IV2PlanStore? v2PlanStore = null,
        IPlaybackLiveWiringDiagnosticsStore? liveWiringDiagnosticsStore = null,
        PlaybackOperationalMetrics? operationalMetrics = null,
        IRequestCorrelationAccessor? requestCorrelation = null)
    {
        _requestCorrelation = requestCorrelation ?? NullRequestCorrelationAccessor.Instance;
        _planner = planner;
        _transcodeManager = transcodeManager;
        _sessionManager = sessionManager;
        _diagnosticsStore = diagnosticsStore ?? NoOpShadowDiagnosticsStore.Instance;
        _v2PlanStore = v2PlanStore ?? NoOpV2PlanStore.Instance;
        _liveWiringDiagnosticsStore = liveWiringDiagnosticsStore ?? NoOpPlaybackLiveWiringDiagnosticsStore.Instance;
        _operationalMetrics = operationalMetrics ?? new PlaybackOperationalMetrics();
        transcodeManager.TranscodingJobEnded += OnTranscodingJobEnded;
        transcodeManager.TranscodingJobStarted += OnTranscodingJobStarted;
        sessionManager.PlaybackStart += OnPlaybackStart;
        sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sweepTimer = new Timer(_ => SweepExpired(DateTimeOffset.UtcNow), null, SweepInterval, SweepInterval);
    }

    /// <inheritdoc/>
    public PlaybackSession? Create(PlaybackSessionRequest request, string? playSessionId = null)
    {
        PlaybackPlan? plan;
        ShadowDiagnosticRecord? captured;
        V2PlanRecord? v2Captured;
        using (_diagnosticsStore.BeginCapture())
        using (_v2PlanStore.BeginCapture())
        {
            plan = Plan(request);
            captured = _diagnosticsStore.TakeCaptured();
            v2Captured = _v2PlanStore.TakeCaptured();
        }

        if (plan is null)
        {
            return null;
        }

        var session = StoreOrReplace(request.Kind, playSessionId, request, plan);

        // PR113a: attach-or-remove unconditionally, not just attach-if-captured. StoreOrReplace
        // reuses the existing session id when playSessionId matches an in-flight session, so a
        // successful replan with no new capture (shadow disabled/not sampled this call) must evict
        // whatever record was retained for a *previous* capture on that same id - otherwise the
        // admin diagnostics endpoint would show the new plan next to a stale context/capabilities/
        // reasoning from an earlier call. Remove is a documented no-op when nothing is retained, so
        // this is safe (if slightly redundant) for the brand-new-session case too.
        if (captured is not null)
        {
            _diagnosticsStore.Attach(session.Id, captured);
        }
        else
        {
            _diagnosticsStore.Remove(session.Id);
        }

        // PR115a: same attach-or-remove discipline for the authoritative v2 record. A replan that is
        // no longer authoritative (mode changed, cohort resized) must evict the stale authoritative
        // record, or the session would keep being served a v2 plan its current configuration no
        // longer grants.
        if (v2Captured is not null)
        {
            _v2PlanStore.Attach(session.Id, v2Captured);
        }
        else
        {
            _v2PlanStore.Remove(session.Id);
        }

        return session;
    }

    /// <inheritdoc/>
    public PlaybackSession? Patch(PlaybackSessionId id, PlaybackSessionRequest request)
    {
        PlaybackPlan? plan;
        ShadowDiagnosticRecord? captured;
        V2PlanRecord? v2Captured;
        using (_diagnosticsStore.BeginCapture())
        using (_v2PlanStore.BeginCapture())
        {
            plan = Plan(request);
            captured = _diagnosticsStore.TakeCaptured();
            v2Captured = _v2PlanStore.TakeCaptured();
        }

        if (plan is null)
        {
            return null;
        }

        PlaybackSession updated;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var existing))
            {
                return null;
            }

            updated = existing with { Kind = request.Kind, Request = request, Plan = plan, UpdatedAt = DateTimeOffset.UtcNow };
            _sessions[id] = updated;
        }

        // PR113a: attach-or-remove unconditionally (see the matching comment in Create) - a
        // successful Patch with no new capture must evict whatever record was retained from an
        // earlier Create/Patch on this id, not leave it attached to the freshly replanned session.
        if (captured is not null)
        {
            _diagnosticsStore.Attach(updated.Id, captured);
        }
        else
        {
            _diagnosticsStore.Remove(updated.Id);
        }

        // PR115a: same attach-or-remove discipline for the authoritative v2 record. A replan that is
        // no longer authoritative (mode changed, cohort resized) must evict the stale authoritative
        // record, or the session would keep being served a v2 plan its current configuration no
        // longer grants.
        if (v2Captured is not null)
        {
            _v2PlanStore.Attach(updated.Id, v2Captured);
        }
        else
        {
            _v2PlanStore.Remove(updated.Id);
        }

        return updated;
    }

    /// <inheritdoc/>
    public PlaybackSession Track(PlaybackMediaKind kind, PlaybackPlan plan, string? playSessionId = null)
        => StoreOrReplace(kind, playSessionId, null, plan);

    /// <inheritdoc/>
    public PlaybackSession TrackTranscodeOutput(string outputVideoCodec, string outputAudioCodec, TranscodeReason transcodeReasons, string? playSessionId = null)
    {
        var playMethod = EncodingHelper.IsCopyCodec(outputVideoCodec) && EncodingHelper.IsCopyCodec(outputAudioCodec)
            ? PlayMethod.DirectStream
            : PlayMethod.Transcode;
        return Track(PlaybackMediaKind.Video, new PlaybackPlan(playMethod, transcodeReasons), playSessionId);
    }

    /// <inheritdoc/>
    public bool Delete(PlaybackSessionId id)
    {
        lock (_lock)
        {
            return RemoveNoLock(id);
        }
    }

    /// <inheritdoc/>
    public bool DeleteByPlaySessionId(string playSessionId)
    {
        lock (_lock)
        {
            return _byPlaySessionId.TryGetValue(playSessionId, out var id) && RemoveNoLock(id);
        }
    }

    /// <inheritdoc/>
    public PlaybackSession? Get(PlaybackSessionId id)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(id, out var session) ? session : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<PlaybackSession> GetAll()
    {
        lock (_lock)
        {
            return _sessions.Values.ToList();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transcodeManager.TranscodingJobEnded -= OnTranscodingJobEnded;
        _transcodeManager.TranscodingJobStarted -= OnTranscodingJobStarted;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sweepTimer.Dispose();
    }

    /// <summary>
    /// Removes sessions whose plan has not been refreshed within <see cref="ExpiryTtl"/>. Safety
    /// net for sessions that never receive an <see cref="ITranscodeManager.TranscodingJobEnded"/>
    /// or <see cref="ISessionManager.PlaybackStopped"/> signal. Internal (not private) so tests
    /// can drive it directly instead of depending on the timer's real-time interval.
    /// </summary>
    /// <param name="now">The current time, injected so tests can simulate expiry.</param>
    /// <returns>The number of sessions removed.</returns>
    internal int SweepExpired(DateTimeOffset now)
    {
        lock (_lock)
        {
            var expired = _sessions.Values.Where(s => now - s.UpdatedAt > ExpiryTtl).Select(s => s.Id).ToList();
            foreach (var id in expired)
            {
                RemoveNoLock(id);
            }

            return expired.Count;
        }
    }

    private PlaybackSession StoreOrReplace(PlaybackMediaKind kind, string? playSessionId, PlaybackSessionRequest? request, PlaybackPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(playSessionId)
                && _byPlaySessionId.TryGetValue(playSessionId, out var existingId)
                && _sessions.TryGetValue(existingId, out var existing))
            {
                var updated = existing with { Kind = kind, Request = request, Plan = plan, UpdatedAt = now };
                _sessions[existingId] = updated;
                return updated;
            }

            var session = new PlaybackSession(PlaybackSessionId.NewId(), kind, playSessionId, request, plan, now, now);
            _sessions[session.Id] = session;
            if (!string.IsNullOrEmpty(playSessionId))
            {
                _byPlaySessionId[playSessionId] = session.Id;
            }

            return session;
        }
    }

    private bool RemoveNoLock(PlaybackSessionId id)
    {
        if (!_sessions.Remove(id, out var session))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(session.PlaySessionId))
        {
            _byPlaySessionId.Remove(session.PlaySessionId);

            // PR115d: belt-and-suspenders against a leaked entry - the normal path already removes
            // this in RecordTranscodeStartFailureIfNeverStarted's Ended handling, but a session can
            // be evicted by a DIFFERENT path first (PlaybackStopped arriving before the transcoding
            // job's Ended event, SweepExpired's TTL backstop, an explicit Delete) while a Started was
            // already recorded and no Ended ever follows to clean it up otherwise.
            _startedPlaySessionIds.Remove(session.PlaySessionId);
        }

        // PR113: evicts whatever shadow diagnostic was retained for this session, if any - covers
        // every removal path (Delete, DeleteByPlaySessionId, the transcoding-job-ended/playback-stopped
        // handlers, and SweepExpired), since they all funnel through here.
        _diagnosticsStore.Remove(id);

        // PR115a: evicts whatever authoritative v2 record was retained for this session, if any -
        // same removal paths as the diagnostics store above.
        _v2PlanStore.Remove(id);

        // PR115c: evicts whatever live-wiring outcome was retained for this session, if any - same
        // removal paths as the two stores above.
        _liveWiringDiagnosticsStore.Remove(id);

        return true;
    }

    private void OnTranscodingJobEnded(object? sender, TranscodingJob job)
    {
        if (string.IsNullOrEmpty(job.PlaySessionId))
        {
            return;
        }

        // PR115d: recorded BEFORE DeleteByPlaySessionId below, which evicts (among the other two
        // per-session stores) _liveWiringDiagnosticsStore - the ServedByV2 read this needs. Same
        // ordering constraint RecordLifecycleEvent's callers already respect for the same reason.
        RecordTranscodeStartFailureIfNeverStarted(job.PlaySessionId);
        DeleteByPlaySessionId(job.PlaySessionId);
    }

    /// <summary>
    /// PR113b: records a real "ffmpeg launched" timeline event for the session tied to this job's
    /// play session id, if any is currently tracked. A no-op (not an error) when no tracked session
    /// matches - the job may belong to a live stream or a request this manager never planned.
    /// </summary>
    private void OnTranscodingJobStarted(object? sender, TranscodingJob job)
    {
        // PR115d: records the transcode-start SUCCESS here, at Started time, not at Ended - see
        // RecordTranscodeStartSuccessIfV2Served's remarks for why doing it here (rather than
        // inferring "it must have started" from Ended) avoids a bias toward over-counting failures
        // when a session is evicted (PlaybackStopped, SweepExpired) before its job's Ended event
        // arrives.
        if (!string.IsNullOrEmpty(job.PlaySessionId))
        {
            RecordTranscodeStartSuccessIfV2Served(job.PlaySessionId);
        }

        RecordLifecycleEvent(job.PlaySessionId, "FfmpegStarted");
    }

    /// <summary>
    /// PR113b: records a real "playback started" timeline event for the session tied to this
    /// report's play session id, if any is currently tracked.
    /// </summary>
    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) =>
        RecordLifecycleEvent(e.PlaySessionId, "PlaybackStarted");

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PlaySessionId))
        {
            // PR113b: recorded before eviction, for parity with FfmpegStarted/PlaybackStarted -
            // but RemoveNoLock (reached via DeleteByPlaySessionId below) evicts every retained
            // PlaybackLifecycleEvent for this session, this one included, immediately afterward.
            // The admin diagnostics endpoint can therefore never actually observe a
            // "PlaybackStopped" entry: by the time a session stops, RemoveNoLock has already
            // deleted the session itself, so PlaybackSessionManager.Get(id) - and with it
            // GetPlaybackSession - returns 404 regardless of what the event store retains. This is
            // the pre-existing PR113 removal-on-stop design, unchanged here; recording the event
            // anyway keeps the three signals handled uniformly and is covered at the store level by
            // tests that read it back before the delete call that immediately follows.
            RecordLifecycleEvent(e.PlaySessionId, "PlaybackStopped");
            DeleteByPlaySessionId(e.PlaySessionId);
        }
    }

    /// <summary>
    /// Correlates <paramref name="playSessionId"/> to a currently tracked session id and, if found,
    /// records <paramref name="stage"/> as observed right now. Silently does nothing for an unknown
    /// or null/empty play session id - a lifecycle event that cannot be correlated is simply
    /// dropped, never thrown, matching every other diagnostics-retention failure mode in this class.
    /// </summary>
    private void RecordLifecycleEvent(string? playSessionId, string stage)
    {
        if (string.IsNullOrEmpty(playSessionId))
        {
            return;
        }

        PlaybackSessionId id;
        lock (_lock)
        {
            if (!_byPlaySessionId.TryGetValue(playSessionId, out id))
            {
                return;
            }
        }

        // Issue #42: the request id is stamped per EVENT, alongside (never instead of) the
        // session-scoped grouping key `id`. Usually null here — these three signals arrive from
        // ffmpeg and session callbacks, not from a request — and null is the honest answer.
        _diagnosticsStore.RecordEvent(
            id,
            new PlaybackLifecycleEvent(stage, DateTimeOffset.UtcNow, _requestCorrelation.CurrentRequestId));
    }

    /// <summary>
    /// PR115d: called from <see cref="OnTranscodingJobStarted"/> - correlates
    /// <paramref name="playSessionId"/> to a currently tracked session and, only when that session
    /// was actually served by v2 (per <see cref="_liveWiringDiagnosticsStore"/>), records a
    /// SUCCESSFUL ffmpeg transcode start into <see cref="_operationalMetrics"/> immediately.
    /// </summary>
    /// <remarks>
    /// Deliberately recorded here, at Started time, rather than inferred at Ended time from
    /// "<see cref="_startedPlaySessionIds"/> contains this id". A session can be evicted by a path
    /// unrelated to this transcoding job - <see cref="OnPlaybackStopped"/> (the ordinary "user
    /// stopped playback" signal) or <see cref="SweepExpired"/>'s TTL backstop - strictly BETWEEN this
    /// job's Started and Ended events. Recording the success at Ended time would then find no
    /// correlatable session (evicted) and silently drop it, while a job that never starts at all
    /// produces no playback and therefore no <see cref="OnPlaybackStopped"/> signal, so ITS failure
    /// (recorded by <see cref="RecordTranscodeStartFailureIfNeverStarted"/>) is essentially never
    /// dropped the same way. Recording successes only at Ended would therefore have biased the
    /// observed rate upward (successes silently dropped more often than failures) - exactly the
    /// wrong direction for a rate a stop-threshold guard reads to decide whether to force legacy for
    /// the whole cohort. Recording the success here, while the session is still guaranteed tracked
    /// (this is the very job whose launch <see cref="ITranscodeManager.TranscodingJobStarted"/> is
    /// reporting), removes that race entirely.
    /// </remarks>
    /// <param name="playSessionId">The play session id the started job belongs to.</param>
    private void RecordTranscodeStartSuccessIfV2Served(string playSessionId)
    {
        PlaybackSessionId id;
        lock (_lock)
        {
            // Marked unconditionally (not gated on correlation/ServedByV2) - RecordTranscodeStartFailureIfNeverStarted
            // needs to know "did Started ever fire for this play session id" regardless of whether
            // this manager can still correlate it to a v2-served session by then.
            _startedPlaySessionIds.Add(playSessionId);

            if (!_byPlaySessionId.TryGetValue(playSessionId, out id))
            {
                return;
            }
        }

        if (_liveWiringDiagnosticsStore.TryGet(id, out var outcome) && outcome is not null && outcome.ServedByV2)
        {
            _operationalMetrics.RecordTranscodeStart(failed: false);
        }
    }

    /// <summary>
    /// PR115d: called from <see cref="OnTranscodingJobEnded"/>, BEFORE the eviction that follows it -
    /// records a FAILED ffmpeg transcode start into <see cref="_operationalMetrics"/> when this job
    /// ended without <see cref="RecordTranscodeStartSuccessIfV2Served"/> ever having observed a
    /// matching <see cref="ITranscodeManager.TranscodingJobStarted"/> for this play session id (i.e.
    /// the successful-start case already fully handled itself, at Started time - see that method's
    /// remarks). Silently does nothing for an unknown play session id or a legacy-served session, the
    /// same "correlate or silently no-op" discipline <see cref="RecordLifecycleEvent"/> already
    /// follows. Idempotent against <see cref="ITranscodeManager.TranscodingJobEnded"/>'s documented
    /// "may be raised more than once for the same job" contract: the caller
    /// (<see cref="OnTranscodingJobEnded"/>) always evicts <paramref name="playSessionId"/> from
    /// <see cref="_byPlaySessionId"/> right after this method returns, so a second Ended event for the
    /// same job finds no tracked session here and records nothing a second time.
    /// </summary>
    /// <param name="playSessionId">The play session id the ended job belongs to.</param>
    private void RecordTranscodeStartFailureIfNeverStarted(string playSessionId)
    {
        PlaybackSessionId id;
        lock (_lock)
        {
            if (_startedPlaySessionIds.Remove(playSessionId))
            {
                // It DID start (Started fired first) - RecordTranscodeStartSuccessIfV2Served already
                // recorded this outcome; nothing left to do here but stop tracking the id.
                return;
            }

            if (!_byPlaySessionId.TryGetValue(playSessionId, out id))
            {
                return;
            }
        }

        if (_liveWiringDiagnosticsStore.TryGet(id, out var outcome) && outcome is not null && outcome.ServedByV2)
        {
            _operationalMetrics.RecordTranscodeStart(failed: true);
        }
    }

    private PlaybackPlan? Plan(PlaybackSessionRequest request) => request.Kind switch
    {
        PlaybackMediaKind.Audio => _planner.PlanAudio(request.Options),
        PlaybackMediaKind.Video => _planner.PlanVideo(request.Options),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown playback media kind."),
    };
}
