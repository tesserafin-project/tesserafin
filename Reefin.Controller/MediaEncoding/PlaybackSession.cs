using System;

namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// A planned playback session: the request that produced it, the resulting plan,
/// and when it was created/last updated. Holds no transcoding-job or process state.
/// </summary>
/// <param name="Id">The session identifier.</param>
/// <param name="Request">The request the current <paramref name="Plan"/> was produced from.</param>
/// <param name="Plan">The current playback plan.</param>
/// <param name="CreatedAt">When the session was first created.</param>
/// <param name="UpdatedAt">When the session was last created or patched.</param>
public sealed record PlaybackSession(
    PlaybackSessionId Id,
    PlaybackSessionRequest Request,
    PlaybackPlan Plan,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
