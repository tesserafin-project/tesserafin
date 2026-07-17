using Reefin.Controller.MediaEncoding;

namespace Reefin.MediaEncoding.Playback;

/// <summary>
/// The default <see cref="IPlaybackLiveWiringDiagnosticsStore"/> used when none is supplied - keeps
/// every pre-PR115c call site (including existing test constructors) compiling and behaving exactly
/// as before: no outcome is ever retained.
/// </summary>
public sealed class NoOpPlaybackLiveWiringDiagnosticsStore : IPlaybackLiveWiringDiagnosticsStore
{
    private NoOpPlaybackLiveWiringDiagnosticsStore()
    {
    }

    /// <summary>
    /// Gets the shared singleton instance. Stateless, so a single instance is safe to reuse
    /// everywhere a default is needed.
    /// </summary>
    public static NoOpPlaybackLiveWiringDiagnosticsStore Instance { get; } = new();

    /// <inheritdoc/>
    public void Record(PlaybackSessionId id, PlaybackLiveWiringOutcome outcome)
    {
        // Intentionally discarded: nothing is ever meaningfully retained.
    }

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out PlaybackLiveWiringOutcome? outcome)
    {
        outcome = null;
        return false;
    }

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id)
    {
        // Nothing is ever retained.
    }
}
