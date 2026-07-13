namespace Reefin.Model.Configuration;

/// <summary>
/// Configures the PR98 shadow mode that dual-runs the v2 playback decision engine
/// (<c>Reefin.Playback.Engine</c>) alongside the legacy <c>StreamBuilder</c>-based planner for
/// comparison. Legacy always remains the source of truth for the plan returned to clients,
/// regardless of these settings; they only control whether/how often/how expensively the shadow
/// comparison itself runs.
/// </summary>
public class PlaybackShadowOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the shadow comparison runs at all. Defaults to
    /// <see langword="false"/>: shadow mode is opt-in, since PR100 removed the previous
    /// always-on behavior for safety.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the fraction of eligible playback decisions (0.0-1.0) that actually run the
    /// shadow comparison when <see cref="Enabled"/> is <see langword="true"/>. 1.0 (the default)
    /// means every decision is shadowed; lower values sample.
    /// </summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the soft time budget, in milliseconds, for a single shadow execution
    /// (capability/source mapping + v2 engine decision + projection/comparison). Exceeding this
    /// budget is logged and counted; it never cancels or otherwise affects the run in progress,
    /// and never affects the legacy result.
    /// </summary>
    public int MaxExecutionMs { get; set; } = 50;
}
