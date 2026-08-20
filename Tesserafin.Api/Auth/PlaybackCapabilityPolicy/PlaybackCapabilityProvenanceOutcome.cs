namespace Tesserafin.Api.Auth.PlaybackCapabilityPolicy;

/// <summary>
/// The three answers <see cref="PlaybackCapabilityProvenance"/> can give (#153-LTV-R1).
/// </summary>
public enum PlaybackCapabilityProvenanceOutcome
{
    /// <summary>
    /// No capability was presented, so there is nothing to carry onward.
    /// </summary>
    NothingToPropagate = 0,

    /// <summary>
    /// A validated capability may be carried into the response body.
    /// </summary>
    Propagate = 1,

    /// <summary>
    /// A capability was presented but nothing validated it. The request is refused.
    /// </summary>
    Refuse = 2
}
