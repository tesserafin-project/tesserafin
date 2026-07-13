using System;
using System.Collections.Generic;
using System.Linq;

namespace Reefin.Playback.Shadow;

/// <summary>
/// Compares a legacy and a v2 <see cref="DecisionVector"/> and classifies the result, per
/// docs/pr93-compatibility-lab.md §4.2. This is a heuristic triage, not a formal equivalence proof:
/// it exists to route divergences to a human, not to certify correctness.
/// </summary>
public static class ShadowComparer
{
    /// <summary>
    /// Compares a legacy and a v2 decision vector and classifies the divergence, if any.
    /// </summary>
    /// <param name="legacy">The legacy planner's projected decision.</param>
    /// <param name="v2">The v2 engine's projected decision.</param>
    /// <returns>The classified divergence, including which axes differ.</returns>
    public static ShadowDivergence Compare(DecisionVector legacy, DecisionVector v2)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(v2);

        var methodDiffers = legacy.Method != v2.Method;
        var streamsDiffer = IndexDiffers(legacy.VideoStreamIndex, v2.VideoStreamIndex)
            || IndexDiffers(legacy.AudioStreamIndex, v2.AudioStreamIndex)
            || IndexDiffers(legacy.SubtitleStreamIndex, v2.SubtitleStreamIndex);

        var onlyLegacyTransforms = Except(legacy.TransformClasses, v2.TransformClasses);
        var onlyV2Transforms = Except(v2.TransformClasses, legacy.TransformClasses);
        var onlyLegacyReasons = Except(legacy.ReasonCategories, v2.ReasonCategories);
        var onlyV2Reasons = Except(v2.ReasonCategories, legacy.ReasonCategories);

        var transformsEqual = onlyLegacyTransforms.Count == 0 && onlyV2Transforms.Count == 0;
        var reasonsEqual = onlyLegacyReasons.Count == 0 && onlyV2Reasons.Count == 0;
        var outputEqual = CodecEquals(legacy.OutputContainer, v2.OutputContainer)
            && CodecEquals(legacy.OutputVideoCodec, v2.OutputVideoCodec)
            && CodecEquals(legacy.OutputAudioCodec, v2.OutputAudioCodec);

        DivergenceClass divergenceClass;
        if (legacy.IsViable == v2.IsViable && !methodDiffers && !streamsDiffer && transformsEqual && reasonsEqual && outputEqual)
        {
            divergenceClass = DivergenceClass.Equivalent;
        }
        else if (IsImprovement(legacy, v2, methodDiffers, onlyV2Transforms, onlyLegacyTransforms))
        {
            divergenceClass = DivergenceClass.ExpectedImprovement;
        }
        else if (IsRegression(legacy, v2, methodDiffers, onlyV2Transforms, onlyLegacyTransforms))
        {
            divergenceClass = DivergenceClass.PotentialRegression;
        }
        else if (!methodDiffers && ((onlyLegacyTransforms.Count > 0 && onlyV2Transforms.Count > 0) || (onlyLegacyReasons.Count > 0 && onlyV2Reasons.Count > 0)))
        {
            divergenceClass = DivergenceClass.Unexplained;
        }
        else if (!methodDiffers && !outputEqual)
        {
            divergenceClass = DivergenceClass.KnownV2Limitation;
        }
        else
        {
            // Total fallback: a divergence that fits no named pattern is, by definition, unexplained
            // (docs/pr93-compatibility-lab.md §4.2 - blocks promotion until triaged).
            divergenceClass = DivergenceClass.Unexplained;
        }

        var summary = BuildSummary(divergenceClass, legacy, v2, streamsDiffer, onlyLegacyTransforms, onlyV2Transforms, onlyLegacyReasons, onlyV2Reasons);

        return new ShadowDivergence(divergenceClass, methodDiffers, streamsDiffer, onlyLegacyTransforms, onlyV2Transforms, onlyLegacyReasons, onlyV2Reasons, summary);
    }

    /// <summary>
    /// A <see langword="null"/> index on either side means "not asserted", not "different": it never
    /// counts as a divergence. Only two differing non-null indices do.
    /// </summary>
    private static bool IndexDiffers(int? a, int? b) => a is not null && b is not null && a != b;

    private static bool CodecEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static HashSet<T> Except<T>(IReadOnlySet<T> from, IReadOnlySet<T> subtract)
    {
        var result = new HashSet<T>(from);
        result.ExceptWith(subtract);
        return result;
    }

    /// <summary>
    /// Work ranking for a normalized method: higher means the pipeline does more. Used to compare
    /// "v2 did less/more work than legacy" when the methods differ.
    /// </summary>
    private static int Rank(NormalizedMethod method) => method switch
    {
        NormalizedMethod.DirectPlay => 0,
        NormalizedMethod.Remux => 1,
        NormalizedMethod.Transcode => 2,
        _ => 2,
    };

    private static bool IsImprovement(
        DecisionVector legacy,
        DecisionVector v2,
        bool methodDiffers,
        IReadOnlySet<TransformClass> onlyV2Transforms,
        IReadOnlySet<TransformClass> onlyLegacyTransforms)
    {
        if (!legacy.IsViable && v2.IsViable)
        {
            return true;
        }

        if (legacy.IsViable && v2.IsViable && methodDiffers && legacy.Method is not null && v2.Method is not null)
        {
            return Rank(v2.Method.Value) < Rank(legacy.Method.Value);
        }

        // Same method (or both non-viable): v2's transforms are a proper subset of legacy's -
        // v2 does everything legacy does, minus something.
        return !methodDiffers && onlyV2Transforms.Count == 0 && onlyLegacyTransforms.Count > 0;
    }

    private static bool IsRegression(
        DecisionVector legacy,
        DecisionVector v2,
        bool methodDiffers,
        IReadOnlySet<TransformClass> onlyV2Transforms,
        IReadOnlySet<TransformClass> onlyLegacyTransforms)
    {
        if (legacy.IsViable && !v2.IsViable)
        {
            return true;
        }

        if (legacy.IsViable && v2.IsViable && methodDiffers && legacy.Method is not null && v2.Method is not null)
        {
            return Rank(v2.Method.Value) > Rank(legacy.Method.Value);
        }

        // Same method (or both non-viable): v2's transforms are a proper superset of legacy's -
        // v2 does everything legacy does, plus something extra.
        return !methodDiffers && onlyLegacyTransforms.Count == 0 && onlyV2Transforms.Count > 0;
    }

    private static string BuildSummary(
        DivergenceClass divergenceClass,
        DecisionVector legacy,
        DecisionVector v2,
        bool streamsDiffer,
        IReadOnlySet<TransformClass> onlyLegacyTransforms,
        IReadOnlySet<TransformClass> onlyV2Transforms,
        IReadOnlySet<ReasonCategory> onlyLegacyReasons,
        IReadOnlySet<ReasonCategory> onlyV2Reasons)
    {
        if (divergenceClass == DivergenceClass.Equivalent)
        {
            return $"legacy and v2 agree: method={legacy.Method}.";
        }

        var parts = new List<string>
        {
            $"method legacy={legacy.Method?.ToString() ?? "n/a"} v2={v2.Method?.ToString() ?? "n/a"}",
        };

        if (streamsDiffer)
        {
            parts.Add("streams differ");
        }

        if (onlyLegacyTransforms.Count > 0)
        {
            parts.Add($"legacy-only transforms=[{string.Join(',', onlyLegacyTransforms.OrderBy(t => t))}]");
        }

        if (onlyV2Transforms.Count > 0)
        {
            parts.Add($"v2-only transforms=[{string.Join(',', onlyV2Transforms.OrderBy(t => t))}]");
        }

        if (onlyLegacyReasons.Count > 0)
        {
            parts.Add($"legacy-only reasons=[{string.Join(',', onlyLegacyReasons.OrderBy(r => r))}]");
        }

        if (onlyV2Reasons.Count > 0)
        {
            parts.Add($"v2-only reasons=[{string.Join(',', onlyV2Reasons.OrderBy(r => r))}]");
        }

        if (!CodecEquals(legacy.OutputContainer, v2.OutputContainer))
        {
            parts.Add($"container legacy={legacy.OutputContainer ?? "n/a"} v2={v2.OutputContainer ?? "n/a"}");
        }

        if (!CodecEquals(legacy.OutputVideoCodec, v2.OutputVideoCodec))
        {
            parts.Add($"videoCodec legacy={legacy.OutputVideoCodec ?? "n/a"} v2={v2.OutputVideoCodec ?? "n/a"}");
        }

        if (!CodecEquals(legacy.OutputAudioCodec, v2.OutputAudioCodec))
        {
            parts.Add($"audioCodec legacy={legacy.OutputAudioCodec ?? "n/a"} v2={v2.OutputAudioCodec ?? "n/a"}");
        }

        return $"{divergenceClass}: {string.Join("; ", parts)}";
    }
}
