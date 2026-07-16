using Reefin.Controller.MediaEncoding;
using Reefin.Playback.Execution;

namespace Reefin.MediaEncoding.Playback;

/// <summary>
/// Resolves the v2 <see cref="PlaybackExecutionPlan"/> for a live <see cref="PlaybackSessionId"/>,
/// when one is available (PR114a). Dormant by design: no streaming endpoint consumes this yet -
/// legacy stays the live source of truth until the PR115 canary cutover. This interface exists so the
/// entry point a canary can later switch on is already built, wired into DI, and tested, rather than
/// invented under pressure during that cutover.
/// </summary>
/// <remarks>
/// PR115a: resolves from the AUTHORITATIVE <see cref="V2PlanRecord"/> <see cref="IV2PlanStore"/>
/// retains for a canary/v2 session - never from <see cref="IShadowDiagnosticsStore"/>, which only
/// ever holds observability data and is free to be disabled or evicted without affecting this
/// resolver. The retained record's <see cref="V2PlanRecord.ExecutionPlan"/> is already built at
/// publish time by <c>ShadowPlaybackSessionPlanner</c>/<c>PlaybackSessionManager</c>, so this
/// resolver performs a plain lookup - no <c>PlaybackExecutionPlanBuilder.TryBuild</c> call here.
/// </remarks>
public interface IPlaybackExecutionPlanResolver
{
    /// <summary>
    /// Resolves the v2 execution plan for a session, if one is available.
    /// </summary>
    /// <param name="id">The session to resolve a plan for.</param>
    /// <returns>
    /// The session's <see cref="PlaybackExecutionPlan"/>, or <see langword="null"/> when the session
    /// has no authoritative v2 record (legacy/shadow-only session, out-of-cohort, engine failed), or
    /// when one is retained but its decision was refused by the builder at publish time (the
    /// retained record's <see cref="V2PlanRecord.ExecutionPlan"/> is itself <see langword="null"/>) -
    /// never throws for either case, since both are ordinary, expected outcomes for a dormant
    /// resolver.
    /// </returns>
    PlaybackExecutionPlan? Resolve(PlaybackSessionId id);
}
