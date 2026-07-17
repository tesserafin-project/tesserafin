using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private readonly object _lock = new();
    private readonly Dictionary<PlaybackSessionId, PlaybackSession> _sessions = new();
    private readonly Dictionary<string, PlaybackSessionId> _byPlaySessionId = new(StringComparer.OrdinalIgnoreCase);
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
    public PlaybackSessionManager(
        IPlaybackSessionPlanner planner,
        ITranscodeManager transcodeManager,
        ISessionManager sessionManager,
        IShadowDiagnosticsStore? diagnosticsStore = null,
        IV2PlanStore? v2PlanStore = null)
    {
        _planner = planner;
        _transcodeManager = transcodeManager;
        _sessionManager = sessionManager;
        _diagnosticsStore = diagnosticsStore ?? NoOpShadowDiagnosticsStore.Instance;
        _v2PlanStore = v2PlanStore ?? NoOpV2PlanStore.Instance;
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
        }

        // PR113: evicts whatever shadow diagnostic was retained for this session, if any - covers
        // every removal path (Delete, DeleteByPlaySessionId, the transcoding-job-ended/playback-stopped
        // handlers, and SweepExpired), since they all funnel through here.
        _diagnosticsStore.Remove(id);

        // PR115a: evicts whatever authoritative v2 record was retained for this session, if any -
        // same removal paths as the diagnostics store above.
        _v2PlanStore.Remove(id);

        return true;
    }

    private void OnTranscodingJobEnded(object? sender, TranscodingJob job)
    {
        if (!string.IsNullOrEmpty(job.PlaySessionId))
        {
            DeleteByPlaySessionId(job.PlaySessionId);
        }
    }

    /// <summary>
    /// PR113b: records a real "ffmpeg launched" timeline event for the session tied to this job's
    /// play session id, if any is currently tracked. A no-op (not an error) when no tracked session
    /// matches - the job may belong to a live stream or a request this manager never planned.
    /// </summary>
    private void OnTranscodingJobStarted(object? sender, TranscodingJob job) =>
        RecordLifecycleEvent(job.PlaySessionId, "FfmpegStarted");

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

        _diagnosticsStore.RecordEvent(id, new PlaybackLifecycleEvent(stage, DateTimeOffset.UtcNow));
    }

    private PlaybackPlan? Plan(PlaybackSessionRequest request) => request.Kind switch
    {
        PlaybackMediaKind.Audio => _planner.PlanAudio(request.Options),
        PlaybackMediaKind.Video => _planner.PlanVideo(request.Options),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown playback media kind."),
    };
}
