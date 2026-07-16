using System;
using System.Collections.Concurrent;
using System.Threading;
using Reefin.Controller.MediaEncoding;

namespace Reefin.MediaEncoding.Playback;

/// <summary>
/// Default <see cref="IShadowDiagnosticsStore"/> implementation: an in-process, thread-safe
/// dictionary of retained records plus the <see cref="AsyncLocal{T}"/>-backed ambient capture scope
/// used to correlate a shadow run with the session id minted after it (see the interface remarks).
/// </summary>
public sealed class InMemoryShadowDiagnosticsStore : IShadowDiagnosticsStore
{
    // The ambient slot holds a mutable AmbientState instance while a BeginCapture scope is open,
    // and null otherwise. The null-vs-instance distinction (rather than storing the record
    // directly) is what lets Publish tell "no scope is open" apart from "a scope is open but
    // nothing has been published yet" - both of which would otherwise look like a null record.
    private readonly AsyncLocal<AmbientState?> _ambient = new();
    private readonly ConcurrentDictionary<PlaybackSessionId, ShadowDiagnosticRecord> _records = new();

    /// <inheritdoc/>
    public IDisposable BeginCapture()
    {
        // PR113a: save the enclosing scope's state (null if there is none) so a nested
        // BeginCapture - however unlikely in the current single-level Create/Patch usage -
        // restores the exact parent state on Dispose instead of clobbering it with null.
        var previous = _ambient.Value;
        _ambient.Value = new AmbientState();
        return new CaptureScope(_ambient, previous);
    }

    /// <inheritdoc/>
    public void Publish(ShadowDiagnosticRecord record)
    {
        var state = _ambient.Value;
        if (state is null)
        {
            // PR113a: no BeginCapture scope is open on this async flow. This should not happen
            // from ShadowPlaybackSessionPlanner (it only calls Publish from inside the scope
            // PlaybackSessionManager opens around Plan()), but guard it anyway rather than
            // silently corrupting some unrelated caller's ambient slot. This type has no
            // ILogger (see class remarks) and shadow diagnostics are best-effort, so the record
            // is dropped instead of thrown - a lost diagnostic must never fail live playback.
            return;
        }

        state.Record = record;
    }

    /// <inheritdoc/>
    public ShadowDiagnosticRecord? TakeCaptured() => _ambient.Value?.Record;

    /// <inheritdoc/>
    public void Attach(PlaybackSessionId id, ShadowDiagnosticRecord record) => _records[id] = record;

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out ShadowDiagnosticRecord? record) => _records.TryGetValue(id, out record);

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id) => _records.TryRemove(id, out _);

    private sealed class AmbientState
    {
        public ShadowDiagnosticRecord? Record { get; set; }
    }

    private sealed class CaptureScope : IDisposable
    {
        private readonly AsyncLocal<AmbientState?> _ambient;
        private readonly AmbientState? _previous;
        private bool _disposed;

        public CaptureScope(AsyncLocal<AmbientState?> ambient, AmbientState? previous)
        {
            _ambient = ambient;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ambient.Value = _previous;
        }
    }
}
