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
    public PlaybackSessionManager(IPlaybackSessionPlanner planner, ITranscodeManager transcodeManager, ISessionManager sessionManager)
    {
        _planner = planner;
        _transcodeManager = transcodeManager;
        _sessionManager = sessionManager;
        transcodeManager.TranscodingJobEnded += OnTranscodingJobEnded;
        sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sweepTimer = new Timer(_ => SweepExpired(DateTimeOffset.UtcNow), null, SweepInterval, SweepInterval);
    }

    /// <inheritdoc/>
    public PlaybackSession? Create(PlaybackSessionRequest request, string? playSessionId = null)
    {
        var plan = Plan(request);
        return plan is null ? null : StoreOrReplace(request.Kind, playSessionId, request, plan);
    }

    /// <inheritdoc/>
    public PlaybackSession? Patch(PlaybackSessionId id, PlaybackSessionRequest request)
    {
        var plan = Plan(request);
        if (plan is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var existing))
            {
                return null;
            }

            var updated = existing with { Kind = request.Kind, Request = request, Plan = plan, UpdatedAt = DateTimeOffset.UtcNow };
            _sessions[id] = updated;
            return updated;
        }
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

        return true;
    }

    private void OnTranscodingJobEnded(object? sender, TranscodingJob job)
    {
        if (!string.IsNullOrEmpty(job.PlaySessionId))
        {
            DeleteByPlaySessionId(job.PlaySessionId);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PlaySessionId))
        {
            DeleteByPlaySessionId(e.PlaySessionId);
        }
    }

    private PlaybackPlan? Plan(PlaybackSessionRequest request) => request.Kind switch
    {
        PlaybackMediaKind.Audio => _planner.PlanAudio(request.Options),
        PlaybackMediaKind.Video => _planner.PlanVideo(request.Options),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown playback media kind."),
    };
}
