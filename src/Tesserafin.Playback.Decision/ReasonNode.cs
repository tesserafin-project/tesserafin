using System.Collections.Generic;

namespace Tesserafin.Playback.Decision;

/// <summary>
/// One node in the structured explanation tree for a <see cref="PlaybackDecision"/>: what was
/// evaluated, what came of it, and (via <see cref="Children"/>) what sub-reasons led there. This
/// replaces a flat reason enum with causality: not just which walls were hit, but why the chosen
/// method follows from them.
/// </summary>
/// <param name="Code">The stable, serializable reason code for this node.</param>
/// <param name="Outcome">The result recorded for <paramref name="Subject"/>.</param>
/// <param name="Subject">What this node is reasoning about.</param>
/// <param name="Detail">Free-form detail (for example observed vs. expected values), or <see langword="null"/> if none.</param>
/// <param name="Children">The sub-reasons that led to this node, or empty for a leaf.</param>
public sealed record ReasonNode(
    ReasonCode Code,
    ReasonOutcome Outcome,
    ReasonSubject Subject,
    string? Detail,
    IReadOnlyList<ReasonNode> Children)
{
    /// <summary>
    /// Creates a leaf node with no children.
    /// </summary>
    /// <param name="code">The stable, serializable reason code for this node.</param>
    /// <param name="outcome">The result recorded for <paramref name="subject"/>.</param>
    /// <param name="subject">What this node is reasoning about.</param>
    /// <param name="detail">Free-form detail, or <see langword="null"/> if none.</param>
    /// <returns>A <see cref="ReasonNode"/> with an empty <see cref="Children"/> list.</returns>
    public static ReasonNode Leaf(ReasonCode code, ReasonOutcome outcome, ReasonSubject subject, string? detail = null) =>
        new(code, outcome, subject, detail, []);
}
