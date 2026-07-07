using System;
using System.Collections.Generic;
using Reefin.Controller.MediaEncoding;

namespace Reefin.MediaEncoding.Playback;

/// <inheritdoc cref="IPlaybackSessionManager"/>
public class PlaybackSessionManager : IPlaybackSessionManager
{
    private readonly IPlaybackSessionPlanner _planner;
    private readonly object _lock = new();
    private readonly Dictionary<PlaybackSessionId, PlaybackSession> _sessions = new();
    private readonly Dictionary<string, PlaybackSessionId> _byPlaySessionId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionManager"/> class.
    /// </summary>
    /// <param name="planner">Instance of the <see cref="IPlaybackSessionPlanner"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    public PlaybackSessionManager(IPlaybackSessionPlanner planner, ITranscodeManager transcodeManager)
    {
        _planner = planner;
        transcodeManager.TranscodingJobEnded += OnTranscodingJobEnded;
    }

    /// <inheritdoc/>
    public PlaybackSession? Create(PlaybackSessionRequest request)
    {
        var plan = Plan(request);
        if (plan is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var session = new PlaybackSession(PlaybackSessionId.NewId(), request.Kind, null, request, plan, now, now);
        lock (_lock)
        {
            _sessions[session.Id] = session;
        }

        return session;
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
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(playSessionId)
                && _byPlaySessionId.TryGetValue(playSessionId, out var existingId)
                && _sessions.TryGetValue(existingId, out var existing))
            {
                var updated = existing with { Kind = kind, Plan = plan, UpdatedAt = now };
                _sessions[existingId] = updated;
                return updated;
            }

            var session = new PlaybackSession(PlaybackSessionId.NewId(), kind, playSessionId, null, plan, now, now);
            _sessions[session.Id] = session;
            if (!string.IsNullOrEmpty(playSessionId))
            {
                _byPlaySessionId[playSessionId] = session.Id;
            }

            return session;
        }
    }

    /// <inheritdoc/>
    public bool Delete(PlaybackSessionId id)
    {
        lock (_lock)
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
    }

    /// <inheritdoc/>
    public bool DeleteByPlaySessionId(string playSessionId)
    {
        lock (_lock)
        {
            if (!_byPlaySessionId.Remove(playSessionId, out var id))
            {
                return false;
            }

            return _sessions.Remove(id);
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

    private void OnTranscodingJobEnded(object? sender, TranscodingJob job)
    {
        if (!string.IsNullOrEmpty(job.PlaySessionId))
        {
            DeleteByPlaySessionId(job.PlaySessionId);
        }
    }

    private PlaybackPlan? Plan(PlaybackSessionRequest request) => request.Kind switch
    {
        PlaybackMediaKind.Audio => _planner.PlanAudio(request.Options),
        PlaybackMediaKind.Video => _planner.PlanVideo(request.Options),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown playback media kind."),
    };
}
