using System;
using System.Collections.Concurrent;
using Reefin.Controller.MediaEncoding;

namespace Reefin.MediaEncoding.Playback;

/// <inheritdoc cref="IPlaybackSessionManager"/>
public class PlaybackSessionManager : IPlaybackSessionManager
{
    private readonly IPlaybackSessionPlanner _planner;
    private readonly ConcurrentDictionary<PlaybackSessionId, PlaybackSession> _sessions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionManager"/> class.
    /// </summary>
    /// <param name="planner">Instance of the <see cref="IPlaybackSessionPlanner"/> interface.</param>
    public PlaybackSessionManager(IPlaybackSessionPlanner planner)
    {
        _planner = planner;
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
        var session = new PlaybackSession(PlaybackSessionId.NewId(), request.Kind, request, plan, now, now);
        _sessions[session.Id] = session;
        return session;
    }

    /// <inheritdoc/>
    public PlaybackSession? Patch(PlaybackSessionId id, PlaybackSessionRequest request)
    {
        if (!_sessions.TryGetValue(id, out var existing))
        {
            return null;
        }

        var plan = Plan(request);
        if (plan is null)
        {
            return null;
        }

        var updated = existing with { Kind = request.Kind, Request = request, Plan = plan, UpdatedAt = DateTimeOffset.UtcNow };
        _sessions[id] = updated;
        return updated;
    }

    /// <inheritdoc/>
    public PlaybackSession Track(PlaybackMediaKind kind, PlaybackPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new PlaybackSession(PlaybackSessionId.NewId(), kind, null, plan, now, now);
        _sessions[session.Id] = session;
        return session;
    }

    /// <inheritdoc/>
    public bool Delete(PlaybackSessionId id) => _sessions.TryRemove(id, out _);

    /// <inheritdoc/>
    public PlaybackSession? Get(PlaybackSessionId id) => _sessions.TryGetValue(id, out var session) ? session : null;

    private PlaybackPlan? Plan(PlaybackSessionRequest request) => request.Kind switch
    {
        PlaybackMediaKind.Audio => _planner.PlanAudio(request.Options),
        PlaybackMediaKind.Video => _planner.PlanVideo(request.Options),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown playback media kind."),
    };
}
