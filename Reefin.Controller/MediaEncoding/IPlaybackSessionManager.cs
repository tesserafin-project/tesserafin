using Reefin.Model.Session;

namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// Tracks the lifecycle of planned playback sessions (create/patch/delete), on top of
/// <see cref="IPlaybackSessionPlanner"/> decisions. This is the session bookkeeping half
/// of the point-1 protocol, kept separate from the pure planning decision so each can be
/// verified independently. Sessions tied to a play session id are ended automatically when
/// the corresponding transcoding job ends (via <see cref="ITranscodeManager.TranscodingJobEnded"/>)
/// or playback stops (direct play has no transcoding job to signal this). A time-to-live sweep
/// is the backstop for sessions that never receive either signal (e.g. a PlaybackInfo probe with
/// no playback that follows).
/// </summary>
public interface IPlaybackSessionManager
{
    /// <summary>
    /// Plans and stores a new session.
    /// </summary>
    /// <param name="request">The request to plan.</param>
    /// <param name="playSessionId">
    /// The client-facing play session id, when known. At most one session is kept per play
    /// session id: creating with the same id again replaces that session's plan and request,
    /// and the session is removed automatically when the transcoding job with that id ends.
    /// </param>
    /// <returns>The created (or updated) session, or <c>null</c> if no viable plan exists.</returns>
    PlaybackSession? Create(PlaybackSessionRequest request, string? playSessionId = null);

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
    /// <param name="playSessionId">
    /// The client-supplied play session id, when known. At most one session is kept per play
    /// session id: tracking the same id again replaces that session's plan, and the session is
    /// removed automatically when the transcoding job with that id ends.
    /// </param>
    /// <returns>The created (or updated) session.</returns>
    PlaybackSession Track(PlaybackMediaKind kind, PlaybackPlan plan, string? playSessionId = null);

    /// <summary>
    /// Tracks a video session, deriving <see cref="PlayMethod"/> from output codecs a caller has
    /// already decided on (both a copy codec means <see cref="PlayMethod.DirectStream"/>,
    /// otherwise <see cref="PlayMethod.Transcode"/> — see <see cref="EncodingHelper.IsCopyCodec"/>).
    /// Use this instead of deriving <see cref="PlaybackPlan"/> by hand and calling
    /// <see cref="Track"/> — e.g. a controller, such as the HLS variant-playlist endpoint, that
    /// only sees the already-decided output codecs, not a full plan.
    /// </summary>
    /// <param name="outputVideoCodec">The output video codec, e.g. "copy" or "h264".</param>
    /// <param name="outputAudioCodec">The output audio codec, e.g. "copy" or "aac".</param>
    /// <param name="transcodeReasons">Why transcoding was needed, if it was.</param>
    /// <param name="playSessionId">
    /// The client-supplied play session id, when known. Same dedup semantics as <see cref="Track"/>.
    /// </param>
    /// <returns>The created (or updated) session.</returns>
    PlaybackSession TrackTranscodeOutput(string outputVideoCodec, string outputAudioCodec, TranscodeReason transcodeReasons, string? playSessionId = null);

    /// <summary>
    /// Removes a session.
    /// </summary>
    /// <param name="id">The session to remove.</param>
    /// <returns><c>true</c> if a session was removed; <c>false</c> if it did not exist.</returns>
    bool Delete(PlaybackSessionId id);

    /// <summary>
    /// Removes the session tied to the given play session id, if any.
    /// </summary>
    /// <param name="playSessionId">The play session id.</param>
    /// <returns><c>true</c> if a session was removed; <c>false</c> if none was tied to that id.</returns>
    bool DeleteByPlaySessionId(string playSessionId);

    /// <summary>
    /// Gets a session by id, for diagnostics.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <returns>The session, or <c>null</c> if it does not exist.</returns>
    PlaybackSession? Get(PlaybackSessionId id);
}
