using System;
using Reefin.Controller.MediaEncoding;
using Reefin.Playback.Execution;

namespace Reefin.MediaEncoding.Playback;

/// <inheritdoc cref="IPlaybackExecutionPlanResolver"/>
public sealed class PlaybackExecutionPlanResolver : IPlaybackExecutionPlanResolver
{
    private readonly IV2PlanStore _v2PlanStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackExecutionPlanResolver"/> class.
    /// </summary>
    /// <param name="v2PlanStore">
    /// PR115a: the store to read a session's retained AUTHORITATIVE <see cref="V2PlanRecord"/> from -
    /// the same singleton <see cref="PlaybackSessionManager"/> attaches records to. This resolver no
    /// longer reads <see cref="IShadowDiagnosticsStore"/>: that store only ever holds observability
    /// data, never an authoritative decision.
    /// </param>
    public PlaybackExecutionPlanResolver(IV2PlanStore v2PlanStore)
    {
        ArgumentNullException.ThrowIfNull(v2PlanStore);
        _v2PlanStore = v2PlanStore;
    }

    /// <inheritdoc/>
    public PlaybackExecutionPlan? Resolve(PlaybackSessionId id)
    {
        // PR115a: the resolver now reads the session's AUTHORITATIVE v2 plan, never the shadow
        // diagnostics store. The plan is already built at publish time by the planner (only when
        // v2 was authoritative for that call), so there is no TryBuild here anymore - just a lookup.
        // Returns null uniformly whether no record is retained at all, or one is retained but its
        // decision was refused by the builder at publish time (ExecutionPlan null on the record).
        return _v2PlanStore.TryGet(id, out var record) ? record?.ExecutionPlan : null;
    }
}
