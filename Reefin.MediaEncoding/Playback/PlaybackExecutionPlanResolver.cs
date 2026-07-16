using System;
using Reefin.Controller.MediaEncoding;
using Reefin.Playback.Execution;

namespace Reefin.MediaEncoding.Playback;

/// <inheritdoc cref="IPlaybackExecutionPlanResolver"/>
public sealed class PlaybackExecutionPlanResolver : IPlaybackExecutionPlanResolver
{
    private readonly IShadowDiagnosticsStore _diagnosticsStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackExecutionPlanResolver"/> class.
    /// </summary>
    /// <param name="diagnosticsStore">
    /// The store to read a session's retained <see cref="ShadowDiagnosticRecord"/> from - the same
    /// singleton <see cref="PlaybackSessionManager"/> attaches records to.
    /// </param>
    public PlaybackExecutionPlanResolver(IShadowDiagnosticsStore diagnosticsStore)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsStore);
        _diagnosticsStore = diagnosticsStore;
    }

    /// <inheritdoc/>
    public PlaybackExecutionPlan? Resolve(PlaybackSessionId id)
    {
        if (!_diagnosticsStore.TryGet(id, out var record) || record is null)
        {
            return null;
        }

        // PR114a: the non-throwing builder entry point on purpose - a refused decision (for example
        // NotViable, or a shape the builder does not recognize as executable) is an ordinary "nothing
        // to resolve" outcome here, exactly like no record being retained at all. Never propagates
        // PlaybackExecutionPlanRefusedException: this resolver is not yet on any live path, but it
        // must still behave like a well-behaved diagnostics-adjacent read, not a decision-maker that
        // can fail.
        return PlaybackExecutionPlanBuilder.TryBuild(record.Decision, out var plan, out _) ? plan : null;
    }
}
