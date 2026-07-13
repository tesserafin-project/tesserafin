namespace Reefin.Playback.Shadow;

/// <summary>
/// The classification <see cref="ShadowComparer"/> assigns to a divergence between a legacy and a
/// v2 <see cref="DecisionVector"/>, per docs/pr93-compatibility-lab.md §4.2. This is a heuristic,
/// not a formal proof: it triages divergences for human review rather than certifying correctness.
/// </summary>
public enum DivergenceClass
{
    /// <summary>
    /// The two vectors are identical. Note: legacy <c>PlayMethod.DirectStream</c> is normalized to
    /// <see cref="NormalizedMethod.Remux"/>, so a DirectStream-vs-Remux pairing is
    /// <see cref="Equivalent"/>, not a divergence.
    /// </summary>
    Equivalent,

    /// <summary>
    /// v2 does strictly less work than legacy, or succeeds where legacy failed: a genuine
    /// improvement.
    /// </summary>
    ExpectedImprovement,

    /// <summary>
    /// v2 is less capable than legacy in a documented, already-understood way.
    /// </summary>
    KnownV2Limitation,

    /// <summary>
    /// v2 does strictly more work than legacy, or fails where legacy succeeded: v2 appears worse and
    /// unexplained; needs investigation.
    /// </summary>
    PotentialRegression,

    /// <summary>
    /// The divergence does not fit any other classification. Per docs/pr93-compatibility-lab.md §4.2
    /// this blocks promotion (PR108) until triaged.
    /// </summary>
    Unexplained,
}
