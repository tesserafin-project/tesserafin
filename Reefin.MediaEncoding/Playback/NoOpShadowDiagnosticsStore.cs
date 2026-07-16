using System;
using System.Collections.Generic;
using Reefin.Controller.MediaEncoding;

namespace Reefin.MediaEncoding.Playback;

/// <summary>
/// The default <see cref="IShadowDiagnosticsStore"/> used when none is supplied - by
/// <see cref="PlaybackSessionManager"/>'s and <see cref="ShadowPlaybackSessionPlanner"/>'s existing
/// constructors that predate PR113, kept for source/binary compatibility. Retains nothing and
/// captures nothing: <see cref="TryGet"/> always reports no record, matching the pre-PR113 behavior
/// of there being no diagnostic to retain at all.
/// </summary>
public sealed class NoOpShadowDiagnosticsStore : IShadowDiagnosticsStore
{
    private NoOpShadowDiagnosticsStore()
    {
    }

    /// <summary>
    /// Gets the shared singleton instance. Stateless, so a single instance is safe to reuse
    /// everywhere a default is needed.
    /// </summary>
    public static NoOpShadowDiagnosticsStore Instance { get; } = new();

    /// <inheritdoc/>
    public IDisposable BeginCapture() => NullScope.Instance;

    /// <inheritdoc/>
    public void Publish(ShadowDiagnosticRecord record)
    {
        // Intentionally discarded: no capture scope is ever meaningfully open.
    }

    /// <inheritdoc/>
    public ShadowDiagnosticRecord? TakeCaptured() => null;

    /// <inheritdoc/>
    public void Attach(PlaybackSessionId id, ShadowDiagnosticRecord record)
    {
        // Intentionally discarded.
    }

    /// <inheritdoc/>
    public bool TryGet(PlaybackSessionId id, out ShadowDiagnosticRecord? record)
    {
        record = null;
        return false;
    }

    /// <inheritdoc/>
    public void Remove(PlaybackSessionId id)
    {
        // Nothing is ever retained.
    }

    /// <inheritdoc/>
    public void RecordEvent(PlaybackSessionId id, PlaybackLifecycleEvent lifecycleEvent)
    {
        // Intentionally discarded: no capture is ever meaningfully retained.
    }

    /// <inheritdoc/>
    public IReadOnlyList<PlaybackLifecycleEvent> GetEvents(PlaybackSessionId id) => Array.Empty<PlaybackLifecycleEvent>();

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
