using System.Collections.Generic;

namespace Reefin.Playback.Shadow;

/// <summary>
/// The result of comparing a legacy and a v2 <see cref="DecisionVector"/>: a classification plus
/// the specific axes that differ, for structured logging by the shadow decorator.
/// </summary>
/// <param name="Class">The heuristic classification of this divergence.</param>
/// <param name="MethodDiffers">Whether the normalized method differs between the two vectors.</param>
/// <param name="StreamsDiffer">Whether any selected stream index differs (a <see langword="null"/> on either side never counts as a difference).</param>
/// <param name="OnlyLegacy">The transform classes present in the legacy vector but not the v2 vector.</param>
/// <param name="OnlyV2">The transform classes present in the v2 vector but not the legacy vector.</param>
/// <param name="ReasonOnlyLegacy">The reason categories present in the legacy vector but not the v2 vector.</param>
/// <param name="ReasonOnlyV2">The reason categories present in the v2 vector but not the legacy vector.</param>
/// <param name="Summary">A short, human-readable summary of the divergence, suitable for a single structured log line.</param>
public sealed record ShadowDivergence(
    DivergenceClass Class,
    bool MethodDiffers,
    bool StreamsDiffer,
    IReadOnlySet<TransformClass> OnlyLegacy,
    IReadOnlySet<TransformClass> OnlyV2,
    IReadOnlySet<ReasonCategory> ReasonOnlyLegacy,
    IReadOnlySet<ReasonCategory> ReasonOnlyV2,
    string Summary);
