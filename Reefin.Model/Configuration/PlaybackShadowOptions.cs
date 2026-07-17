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
    private int _canaryPercentage;

    /// <summary>
    /// Gets or sets a value indicating whether the shadow comparison runs at all. Defaults to
    /// <see langword="false"/>: shadow mode is opt-in, since PR100 removed the previous
    /// always-on behavior for safety. PR115a: kept for backward compatibility with configurations
    /// that predate <see cref="Mode"/> - see <see cref="GetEffectiveMode"/> for how the two
    /// combine. New configurations should set <see cref="Mode"/> instead.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the explicit role the v2 engine plays for live playback (PR115a). Defaults to
    /// <see cref="PlaybackEngineMode.Legacy"/>. When left at that default, a pre-PR115a
    /// configuration with <see cref="Enabled"/> set still gets shadow behavior - see
    /// <see cref="GetEffectiveMode"/>.
    /// </summary>
    public PlaybackEngineMode Mode { get; set; } = PlaybackEngineMode.Legacy;

    /// <summary>
    /// Gets or sets the percentage (0-100) of user/device pairs in the canary cohort when
    /// <see cref="Mode"/> is <see cref="PlaybackEngineMode.Canary"/>. Cohort membership is a
    /// deterministic hash of the requesting user and device - the same pair is always in or always
    /// out for a given percentage, never a fresh random draw per request. Clamped to [0, 100] on
    /// set, following this type's PR104 clamp-don't-throw convention. Defaults to 0: enabling
    /// canary mode without choosing a cohort size enrolls nobody.
    /// </summary>
    public int CanaryPercentage
    {
        get => _canaryPercentage;
        set => _canaryPercentage = Math.Clamp(value, 0, 100);
    }

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

    /// <summary>
    /// Resolves the mode this configuration actually asks for: <see cref="Mode"/> verbatim, except
    /// that the <see cref="PlaybackEngineMode.Legacy"/> default combined with the pre-PR115a
    /// <see cref="Enabled"/> flag still means shadow mode, so existing configurations keep their
    /// behavior without being rewritten. A method rather than a property so the XML configuration
    /// serializer never mistakes this derived value for a third persisted knob.
    /// </summary>
    /// <returns>The effective <see cref="PlaybackEngineMode"/>.</returns>
    public PlaybackEngineMode GetEffectiveMode() =>
        Mode == PlaybackEngineMode.Legacy && Enabled ? PlaybackEngineMode.Shadow : Mode;
}
