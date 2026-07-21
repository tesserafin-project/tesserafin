namespace Tesserafin.Playback.Execution;

/// <summary>
/// Why <see cref="PlaybackExecutionPlanBuilder"/> refused to build a <see cref="PlaybackExecutionPlan"/>
/// from a given <c>Tesserafin.Playback.Decision.PlaybackDecision</c>.
/// </summary>
public enum PlaybackExecutionPlanRefusalReason
{
    /// <summary>
    /// The decision is not viable (<c>PlaybackDecision.IsViable</c> is <see langword="false"/>):
    /// the engine found no playable plan for the request, so there is nothing to execute.
    /// </summary>
    NotViable,

    /// <summary>
    /// The decision selects neither a video nor an audio stream. A plan with nothing to play, copy,
    /// or transcode cannot be executed, regardless of how it got into this state.
    /// </summary>
    NoStreamsSelected,

    /// <summary>
    /// The decision's <c>Output.Container</c> is missing. Every viable decision the real engine
    /// produces populates it; a decision that does not is treated as incomplete rather than guessed
    /// at (for example, defaulting to the source's own container) - the builder is a pure projection,
    /// never a second decision-maker.
    /// </summary>
    MissingOutputContainer,
}
