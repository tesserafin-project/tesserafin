namespace Tesserafin.Api.Auth.PlaybackCapabilityPolicy;

/// <summary>
/// What a request may carry into a response body (#153-LTV-R1).
/// </summary>
public sealed class PlaybackCapabilityProvenanceDecision
{
    private PlaybackCapabilityProvenanceDecision(PlaybackCapabilityProvenanceOutcome outcome, ValidatedPlaybackCapability? capability)
    {
        Outcome = outcome;
        Capability = capability;
    }

    /// <summary>
    /// Gets the decision this request produced.
    /// </summary>
    public PlaybackCapabilityProvenanceOutcome Outcome { get; }

    /// <summary>
    /// Gets the capability to propagate, non-null only when <see cref="Outcome"/> is
    /// <see cref="PlaybackCapabilityProvenanceOutcome.Propagate"/>.
    /// </summary>
    public ValidatedPlaybackCapability? Capability { get; }

    /// <summary>
    /// Gets the decision for a request that carries no capability anywhere: nothing to propagate,
    /// and nothing wrong with that. A durable-token request looks exactly like this.
    /// </summary>
    public static PlaybackCapabilityProvenanceDecision NothingToPropagate { get; }
        = new(PlaybackCapabilityProvenanceOutcome.NothingToPropagate, null);

    /// <summary>
    /// Gets the decision for a request that presented a capability nothing validated. Serving it
    /// would mean copying an unchecked, caller-controlled string into a response body.
    /// </summary>
    public static PlaybackCapabilityProvenanceDecision Refuse { get; }
        = new(PlaybackCapabilityProvenanceOutcome.Refuse, null);

    /// <summary>
    /// The decision for a request whose capability was validated.
    /// </summary>
    /// <param name="capability">The validated capability.</param>
    /// <returns>The decision.</returns>
    public static PlaybackCapabilityProvenanceDecision Propagate(ValidatedPlaybackCapability capability)
        => new(PlaybackCapabilityProvenanceOutcome.Propagate, capability);
}
