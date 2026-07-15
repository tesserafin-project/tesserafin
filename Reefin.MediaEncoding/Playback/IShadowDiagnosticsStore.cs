using System;
using Reefin.Controller.MediaEncoding;

namespace Reefin.MediaEncoding.Playback;

/// <summary>
/// Retains at most one <see cref="ShadowDiagnosticRecord"/> per live <see cref="PlaybackSessionId"/>,
/// for the admin diagnostics endpoint (docs/pr92-design-playback-api-and-diagnostics.md §4.3, PR113).
/// </summary>
/// <remarks>
/// No session id exists yet when <see cref="ShadowPlaybackSessionPlanner"/> runs the shadow
/// comparison (<c>Plan()</c> is called before <c>PlaybackSessionManager.StoreOrReplace</c> mints
/// one), so correlation happens in two steps: the shadow run publishes its record into an ambient
/// slot via <see cref="Publish"/>, scoped by <see cref="BeginCapture"/> around the synchronous
/// <c>Plan()</c> call; the caller then reads it back with <see cref="TakeCaptured"/> and, once the
/// real id is known, calls <see cref="Attach"/>. This works because the shadow run is entirely
/// synchronous within that scope - an <see cref="System.Threading.AsyncLocal{T}"/> write in a
/// nested synchronous call is visible to the caller after the call returns, and never leaks across
/// concurrent unrelated calls.
/// </remarks>
public interface IShadowDiagnosticsStore
{
    /// <summary>
    /// Opens an ambient capture scope for one synchronous planning call. Resets the ambient slot to
    /// <see langword="null"/> both on entry and when the returned scope is disposed, so no stale
    /// capture from an earlier call can leak into or out of this one.
    /// </summary>
    /// <returns>A disposable that closes the scope.</returns>
    IDisposable BeginCapture();

    /// <summary>
    /// Publishes a record into the currently open ambient capture scope. Called by
    /// <see cref="ShadowPlaybackSessionPlanner"/> only when a shadow run actually executed (behind
    /// its enabled/sampling gate) - never called otherwise, so <see cref="TakeCaptured"/> naturally
    /// returns <see langword="null"/> when shadow mode is off.
    /// </summary>
    /// <param name="record">The record to publish.</param>
    void Publish(ShadowDiagnosticRecord record);

    /// <summary>
    /// Reads (without clearing) whatever was published into the currently open ambient capture
    /// scope during the call this scope wraps.
    /// </summary>
    /// <returns>The captured record, or <see langword="null"/> if nothing was published.</returns>
    ShadowDiagnosticRecord? TakeCaptured();

    /// <summary>
    /// Retains <paramref name="record"/> keyed by <paramref name="id"/>, replacing any previously
    /// retained record for that id.
    /// </summary>
    /// <param name="id">The live session this record diagnoses.</param>
    /// <param name="record">The record to retain.</param>
    void Attach(PlaybackSessionId id, ShadowDiagnosticRecord record);

    /// <summary>
    /// Looks up the record retained for a session, if any.
    /// </summary>
    /// <param name="id">The session to look up.</param>
    /// <param name="record">The retained record, or <see langword="null"/> if none.</param>
    /// <returns><see langword="true"/> if a record was found.</returns>
    bool TryGet(PlaybackSessionId id, out ShadowDiagnosticRecord? record);

    /// <summary>
    /// Evicts the record retained for a session, if any. A no-op if none is retained.
    /// </summary>
    /// <param name="id">The session whose record should be evicted.</param>
    void Remove(PlaybackSessionId id);
}
