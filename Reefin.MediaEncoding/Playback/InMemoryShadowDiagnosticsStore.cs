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
    private readonly AsyncLocal<ShadowDiagnosticRecord?> _ambient = new();
    private readonly ConcurrentDictionary<PlaybackSessionId, ShadowDiagnosticRecord> _records = new();

    /// <inheritdoc/>
    public IDisposable BeginCapture()
    {
        _ambient.Value = null;
        return new CaptureScope(_ambient);
    }

    /// <inheritdoc/>
    public void Publish(ShadowDiagnosticRecord record) => _ambient.Value = record;

    /// <inheritdoc/>
    public ShadowDiagnosticRecord? TakeCaptured() => _ambient.Value;

    /// <inheritdoc/>
    public void Attach(PlaybackSessionId id, ShadowDiagnosticRecord record) => _records[id] = record;

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out ShadowDiagnosticRecord? record) => _records.TryGetValue(id, out record);

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id) => _records.TryRemove(id, out _);

    private sealed class CaptureScope(AsyncLocal<ShadowDiagnosticRecord?> ambient) : IDisposable
    {
        public void Dispose() => ambient.Value = null;
    }
}
