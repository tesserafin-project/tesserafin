using System;

namespace Reefin.Playback.Execution;

/// <summary>
/// Thrown by <see cref="PlaybackExecutionPlanBuilder.Build(Reefin.Playback.Decision.PlaybackDecision)"/>
/// when the source decision is incomplete or not viable, so no <see cref="PlaybackExecutionPlan"/> can
/// be built for it. Carries a structured <see cref="Reason"/> (rather than only a free-text message)
/// so callers - most notably <c>Reefin.MediaEncoding.Playback.IPlaybackExecutionPlanResolver</c>, which
/// must never let a refusal look like a live-path failure - can branch on why without parsing text.
/// </summary>
public sealed class PlaybackExecutionPlanRefusedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackExecutionPlanRefusedException"/> class.
    /// </summary>
    /// <param name="reason">The structured reason the plan was refused.</param>
    /// <param name="message">A human-readable explanation of the refusal.</param>
    public PlaybackExecutionPlanRefusedException(PlaybackExecutionPlanRefusalReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Gets the structured reason the plan was refused.
    /// </summary>
    public PlaybackExecutionPlanRefusalReason Reason { get; }
}
