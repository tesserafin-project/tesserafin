using System;

namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// A planned playback session: the resulting plan and when it was created/last updated.
/// Holds no transcoding-job or process state.
/// </summary>
/// <param name="Id">The session identifier.</param>
/// <param name="Kind">Whether this is an audio or video session.</param>
/// <param name="PlaySessionId">
/// The client-supplied play session id this session is tied to, when known. Used to end the
/// session when the corresponding transcoding job ends.
/// </param>
/// <param name="Request">
/// The request the current <paramref name="Plan"/> was produced from, when planned by
/// <see cref="IPlaybackSessionPlanner"/>. <c>null</c> when the session instead records a
/// decision made elsewhere (see <see cref="IPlaybackSessionManager.Track(PlaybackMediaKind, PlaybackPlan, string, string)"/>).
/// </param>
/// <param name="Plan">The current playback plan.</param>
/// <param name="CreatedAt">When the session was first created.</param>
/// <param name="UpdatedAt">When the session was last created or patched.</param>
/// <param name="PlaybackAttemptId">
/// Issue #43. The opaque, client-supplied identifier of the playback ATTEMPT that produced this
/// session, or <see langword="null"/> when the client sent none (optional for third-party clients).
/// <para>
/// A third scope, distinct from the two identifiers above it and from the per-request
/// <c>RequestId</c> of issue #42:
/// </para>
/// <list type="bullet">
/// <item><description><c>RequestId</c> — one HTTP round-trip. Changes on every request.</description></item>
/// <item><description><b><see cref="PlaybackAttemptId"/></b> — one playback attempt, retries included. Spans several requests, and is already set on the <c>PlaybackInfo</c> call that PRECEDES this session's existence.</description></item>
/// <item><description><see cref="PlaySessionId"/> / <see cref="Id"/> — one server session. Can outlive several attempts.</description></item>
/// </list>
/// <para>
/// Purely diagnostic: never used for authorization, never used to look a session up, and never
/// used to make a playback decision. Trailing and optional, so every pre-#43 construction keeps
/// compiling and means exactly what it meant.
/// </para>
/// </param>
public sealed record PlaybackSession(
    PlaybackSessionId Id,
    PlaybackMediaKind Kind,
    string? PlaySessionId,
    PlaybackSessionRequest? Request,
    PlaybackPlan Plan,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PlaybackAttemptId = null);
