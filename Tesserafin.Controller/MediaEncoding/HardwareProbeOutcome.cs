namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The result of one candidate's real trial encode, as reported back to
/// <see cref="HardwareSelectionPlanner"/>. Carries enough detail for the startup decision to record
/// <em>why</em> a candidate was rejected without the planner having to parse ffmpeg output itself.
/// </summary>
/// <param name="Succeeded">Whether the trial encode completed successfully.</param>
/// <param name="TimedOut">Whether the trial encode was killed for exceeding the probe timeout. A timeout is a failure: an accelerator that cannot encode a one-second 320x240 clip inside the budget is not usable for real transcoding.</param>
/// <param name="FailureCategory">Coarse classification of the failure, or <see cref="FfmpegErrorCategory.Unknown"/> when the probe succeeded or the cause was not recognised.</param>
public sealed record HardwareProbeOutcome(
    bool Succeeded,
    bool TimedOut,
    FfmpegErrorCategory FailureCategory)
{
    /// <summary>
    /// Gets the outcome of a trial encode that completed successfully.
    /// </summary>
    public static HardwareProbeOutcome Success { get; } = new(true, false, FfmpegErrorCategory.Unknown);

    /// <summary>
    /// Creates the outcome of a trial encode that was killed for exceeding the probe timeout.
    /// </summary>
    /// <returns>A timed-out failure outcome.</returns>
    public static HardwareProbeOutcome Timeout() => new(false, true, FfmpegErrorCategory.Unknown);

    /// <summary>
    /// Creates the outcome of a trial encode that failed for a classified reason.
    /// </summary>
    /// <param name="category">The classified failure cause.</param>
    /// <returns>A failure outcome.</returns>
    public static HardwareProbeOutcome Failure(FfmpegErrorCategory category) => new(false, false, category);
}
