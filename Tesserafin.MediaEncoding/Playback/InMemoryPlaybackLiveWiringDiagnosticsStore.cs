using System.Collections.Concurrent;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Default <see cref="IPlaybackLiveWiringDiagnosticsStore"/> implementation: an in-process,
/// thread-safe dictionary of retained outcomes, keyed by session id. No ambient capture is needed -
/// see the interface's remarks for why.
/// </summary>
public sealed class InMemoryPlaybackLiveWiringDiagnosticsStore : IPlaybackLiveWiringDiagnosticsStore
{
    private readonly ConcurrentDictionary<PlaybackSessionId, PlaybackLiveWiringOutcome> _outcomes = new();

    /// <inheritdoc/>
    public void Record(PlaybackSessionId id, PlaybackLiveWiringOutcome outcome) => _outcomes[id] = outcome;

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out PlaybackLiveWiringOutcome? outcome) => _outcomes.TryGetValue(id, out outcome);

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id) => _outcomes.TryRemove(id, out _);
}
