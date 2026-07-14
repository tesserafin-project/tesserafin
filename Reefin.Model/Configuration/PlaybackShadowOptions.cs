using System;

namespace Reefin.Model.Configuration;

/// <summary>
/// Configures the PR98 shadow mode that dual-runs the v2 playback decision engine
/// (<c>Reefin.Playback.Engine</c>) alongside the legacy <c>StreamBuilder</c>-based planner for
/// comparison. Legacy always remains the source of truth for the plan returned to clients,
/// regardless of these settings; they only control whether/how often/how expensively the shadow
/// comparison itself runs.
/// </summary>
/// <remarks>
/// PR104: <see cref="SampleRate"/> and <see cref="MaxExecutionMs"/> clamp out-of-range values on
/// <c>set</c> rather than throwing. <see cref="TrickplayOptions"/> (the closest existing precedent
/// in this configuration surface) performs no validation at all on its own numeric options - there
/// was no established "throw on invalid config" convention to follow here - and this type is a
/// plain configuration POCO bound directly by the options infrastructure, with no logger available
/// to report a corrected value. Clamping is deterministic and idempotent (the same out-of-range
/// input always yields the same in-range result, and clamping an already-clamped value is a no-op),
/// so silently correcting instead of throwing does not hide a transient or ambiguous condition the
/// way it might for, say, a network error - the alternative (throwing from a config binder deep in
/// startup, or from every call site that reads these properties) would be strictly worse for a
/// value that has one obviously-correct in-range interpretation.
/// </remarks>
public class PlaybackShadowOptions
{
    private double _sampleRate = 1.0;
    private int _maxExecutionMs = 50;

    /// <summary>
    /// Gets or sets a value indicating whether the shadow comparison runs at all. Defaults to
    /// <see langword="false"/>: shadow mode is opt-in, since PR100 removed the previous
    /// always-on behavior for safety.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the fraction of eligible playback decisions (0.0-1.0) that actually run the
    /// shadow comparison when <see cref="Enabled"/> is <see langword="true"/>. 1.0 (the default)
    /// means every decision is shadowed; lower values sample. PR104: clamped to [0, 1] on set - a
    /// value outside that range (including NaN, clamped to 0) has no meaningful sampling
    /// interpretation, so it is corrected rather than left to produce nonsensical behavior
    /// downstream (for example a negative rate always sampling out, or a rate above 1 being
    /// indistinguishable from 1).
    /// </summary>
    public double SampleRate
    {
        get => _sampleRate;
        set => _sampleRate = double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Gets or sets the soft time budget, in milliseconds, for a single shadow execution
    /// (capability/source mapping + v2 engine decision + projection/comparison). Exceeding this
    /// budget is logged and counted; it never cancels or otherwise affects the run in progress,
    /// and never affects the legacy result. PR104: clamped to a minimum of 1 on set - a
    /// zero-or-negative budget would mean "every execution always exceeds budget," which is never
    /// the intent of a misconfigured value.
    /// </summary>
    public int MaxExecutionMs
    {
        get => _maxExecutionMs;
        set => _maxExecutionMs = Math.Max(1, value);
    }
}
