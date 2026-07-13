using System.Collections.Generic;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Shadow;

/// <summary>
/// Projects a v2 <see cref="PlaybackDecision"/> into a <see cref="DecisionVector"/> comparable
/// against the legacy planner's decision, per docs/pr93-compatibility-lab.md §4.
/// </summary>
public static class V2DecisionProjector
{
    /// <summary>
    /// Projects a v2 playback decision into a <see cref="DecisionVector"/>.
    /// </summary>
    /// <param name="decision">The v2 engine's decision.</param>
    /// <returns>The equivalent, comparable decision vector.</returns>
    public static DecisionVector Project(PlaybackDecision decision)
    {
        var method = decision.IsViable ? MapMethod(decision.Method) : (NormalizedMethod?)null;
        var transformClasses = MapTransformClasses(decision.Transforms);
        var reasonCategories = new HashSet<ReasonCategory>();
        FoldReasonCategories(decision.Reasoning, reasonCategories);

        return new DecisionVector(
            IsViable: decision.IsViable,
            Method: method,
            VideoStreamIndex: decision.SelectedStreams.Video,
            AudioStreamIndex: decision.SelectedStreams.Audio,
            SubtitleStreamIndex: decision.SelectedStreams.Subtitle?.Index,
            TransformClasses: transformClasses,
            ReasonCategories: reasonCategories,
            OutputContainer: decision.Output.Container,
            OutputVideoCodec: decision.Output.VideoCodec,
            OutputAudioCodec: decision.Output.AudioCodec);
    }

    private static NormalizedMethod MapMethod(PlaybackMethod method) => method switch
    {
        PlaybackMethod.DirectPlay => NormalizedMethod.DirectPlay,
        PlaybackMethod.Remux => NormalizedMethod.Remux,
        PlaybackMethod.Transcode => NormalizedMethod.Transcode,
        _ => NormalizedMethod.Transcode,
    };

    private static HashSet<TransformClass> MapTransformClasses(IReadOnlyList<TransformKind> transforms)
    {
        var classes = new HashSet<TransformClass>();
        foreach (var transform in transforms)
        {
            var mapped = transform switch
            {
                TransformKind.RemuxContainer => TransformClass.Remux,
                TransformKind.TranscodeVideo => TransformClass.TranscodeVideo,
                TransformKind.TranscodeAudio => TransformClass.TranscodeAudio,
                TransformKind.Downmix => TransformClass.Downmix,
                TransformKind.Tonemap => TransformClass.Tonemap,
                TransformKind.BurnInSubtitle => TransformClass.BurnInSubtitle,
                TransformKind.ExtractSubtitle => TransformClass.ExtractSubtitle,

                // CopyVideo/CopyAudio carry no comparable legacy signal; omitted by design.
                TransformKind.CopyVideo => (TransformClass?)null,
                TransformKind.CopyAudio => (TransformClass?)null,
                _ => (TransformClass?)null,
            };

            if (mapped is not null)
            {
                classes.Add(mapped.Value);
            }
        }

        return classes;
    }

    /// <summary>
    /// Recursively flattens a <see cref="ReasonNode"/> tree, mapping each leaf's
    /// <see cref="ReasonCode"/> to its <see cref="ReasonCategory"/> via the shared
    /// <see cref="ReasonCategoryMap"/> and adding it to <paramref name="categories"/>. Positive/
    /// marker codes (<c>MethodChosen</c>, <c>StreamCopyable</c>, <c>SourceSelected</c>,
    /// <c>TonemapRequired</c>, <c>DownmixRequired</c>, <c>SubtitleBurnInRequired</c>) and
    /// <c>NoViablePlan</c> are not in the map and are excluded: viability is already captured by
    /// <see cref="DecisionVector.IsViable"/>.
    /// </summary>
    private static void FoldReasonCategories(ReasonNode node, HashSet<ReasonCategory> categories)
    {
        var category = ReasonCategoryMap.MapByName(node.Code.ToString());
        if (category is not null)
        {
            categories.Add(category.Value);
        }

        foreach (var child in node.Children)
        {
            FoldReasonCategories(child, categories);
        }
    }
}
