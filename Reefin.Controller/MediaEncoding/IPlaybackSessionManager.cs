namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// Tracks the lifecycle of planned playback sessions (create/patch/delete), on top of
/// <see cref="IPlaybackSessionPlanner"/> decisions. Not wired into any controller yet —
/// this is the session bookkeeping half of the point-1 protocol, kept separate from the
/// pure planning decision so each can be verified independently.
/// </summary>
public interface IPlaybackSessionManager
{
    /// <summary>
    /// Plans and stores a new session.
    /// </summary>
    /// <param name="request">The request to plan.</param>
    /// <returns>The created session, or <c>null</c> if no viable plan exists.</returns>
    PlaybackSession? Create(PlaybackSessionRequest request);

    /// <summary>
    /// Re-plans an existing session with a new request, replacing its stored plan.
    /// </summary>
    /// <param name="id">The session to patch.</param>
    /// <param name="request">The new request to plan.</param>
    /// <returns>The updated session, or <c>null</c> if the session does not exist or no viable plan exists.</returns>
    PlaybackSession? Patch(PlaybackSessionId id, PlaybackSessionRequest request);

    /// <summary>
    /// Records a session whose plan was already decided elsewhere — e.g. a controller that
    /// received an already-decided play method and codecs from the client — without invoking
    /// <see cref="IPlaybackSessionPlanner"/>. Use <see cref="Create"/> instead when the plan
    /// should be derived from <see cref="Reefin.Model.Dlna.MediaOptions"/>.
    /// </summary>
    /// <param name="kind">Whether this is an audio or video session.</param>
    /// <param name="plan">The already-decided plan to record.</param>
    /// <returns>The created session.</returns>
    PlaybackSession Track(PlaybackMediaKind kind, PlaybackPlan plan);

    /// <summary>
    /// Removes a session.
    /// </summary>
    /// <param name="id">The session to remove.</param>
    /// <returns><c>true</c> if a session was removed; <c>false</c> if it did not exist.</returns>
    bool Delete(PlaybackSessionId id);

    /// <summary>
    /// Gets a session by id, for diagnostics.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <returns>The session, or <c>null</c> if it does not exist.</returns>
    PlaybackSession? Get(PlaybackSessionId id);
}
