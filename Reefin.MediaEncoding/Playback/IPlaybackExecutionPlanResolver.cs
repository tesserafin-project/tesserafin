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
/// Least-invasive derivation (PR114a design choice): rather than adding a new field to
/// <see cref="PlaybackSession"/> to carry a v2 decision, this resolver derives the plan from whatever
/// <see cref="ShadowDiagnosticRecord"/> <see cref="IShadowDiagnosticsStore"/> already retains for the
/// session id - the same record PR113's admin diagnostics endpoint reads. That record is only
/// retained when the shadow run for that session's planning call actually executed (shadow mode
/// enabled and not sampled out; see <c>ShadowPlaybackSessionPlanner</c>/<c>PlaybackSessionManager</c>),
/// so <see cref="Resolve"/> naturally returns <see langword="null"/> whenever shadow mode did not run
/// for this session - not an error, just nothing to resolve. This piggybacks on infrastructure PR113
/// already built and tested (capture/attach/evict lifecycle) instead of introducing a second,
/// independently-maintained place a v2 decision could be stored per session.
/// </remarks>
public interface IPlaybackExecutionPlanResolver
{
    /// <summary>
    /// Resolves the v2 execution plan for a session, if one is available.
    /// </summary>
    /// <param name="id">The session to resolve a plan for.</param>
    /// <returns>
    /// The session's <see cref="PlaybackExecutionPlan"/>, or <see langword="null"/> when no shadow
    /// diagnostic is retained for the session (shadow mode did not run for it), or when one is
    /// retained but its <see cref="ShadowDiagnosticRecord.Decision"/> is refused by
    /// <see cref="PlaybackExecutionPlanBuilder"/> (for example, a <c>NotViable</c> decision) - never
    /// throws for either case, since both are ordinary, expected outcomes for a dormant resolver.
    /// </returns>
    PlaybackExecutionPlan? Resolve(PlaybackSessionId id);
}
