namespace Tesserafin.Model.Configuration;

/// <summary>
/// The role the v2 playback decision engine (<c>Tesserafin.Playback.Engine</c>) plays for live playback
/// (PR115a). This is the single explicit switch a canary rollout moves through; it supersedes the
/// implicit "shadow on/off" toggle as the statement of intent, while <see cref="PlaybackShadowOptions.Enabled"/>
/// is kept for backward compatibility (see <see cref="PlaybackShadowOptions.GetEffectiveMode"/>).
/// </summary>
public enum PlaybackEngineMode
{
    /// <summary>
    /// The legacy planner is the only engine that runs. v2 does no work at all - not even the
    /// shadow comparison.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// The legacy planner remains the sole source of truth; v2 runs alongside it purely for
    /// observability (classified divergence logging, admin diagnostics), subject to
    /// <see cref="PlaybackShadowOptions.SampleRate"/>. Identical to the PR98-PR114 shadow mode.
    /// </summary>
    Shadow = 1,

    /// <summary>
    /// Sessions in the deterministic canary cohort (see <see cref="PlaybackShadowOptions.CanaryPercentage"/>)
    /// retain the v2 decision and its execution plan authoritatively - independent of the shadow
    /// diagnostics store, which only receives an observability copy. Sessions outside the cohort
    /// behave exactly as <see cref="Shadow"/>.
    /// </summary>
    Canary = 2,

    /// <summary>
    /// Every session retains the v2 decision and its execution plan authoritatively, as
    /// <see cref="Canary"/> does for its cohort. The legacy planner still runs and still produces
    /// the plan the legacy streaming endpoints execute until PR115c wires them to the v2 plan.
    /// </summary>
    V2 = 3,
}
