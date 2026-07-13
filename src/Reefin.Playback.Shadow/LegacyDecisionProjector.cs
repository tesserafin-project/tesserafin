using System.Collections.Generic;
using System.Linq;
using Reefin.Controller.MediaEncoding;
using Reefin.Extensions;
using Reefin.Model.Session;

namespace Reefin.Playback.Shadow;

/// <summary>
/// Projects a legacy <see cref="PlaybackPlan"/> — the output of
/// <c>Reefin.Controller.MediaEncoding.IPlaybackSessionPlanner</c>, itself backed by
/// <c>Reefin.Model.Dlna.StreamBuilder</c> — into a <see cref="DecisionVector"/> comparable against
/// the v2 engine's decision. Legacy has no explicit transform vocabulary (<c>TransformKind</c> is a
/// v2-only concept), so <see cref="DecisionVector.TransformClasses"/> derivation is best-effort: it
/// infers the most likely transform set from <see cref="PlaybackPlan.PlayMethod"/> and the reason
/// categories folded from <see cref="PlaybackPlan.TranscodeReasons"/>, documented as approximate
/// per docs/pr93-compatibility-lab.md §4.
/// </summary>
public static class LegacyDecisionProjector
{
    /// <summary>
    /// Projects a legacy playback plan into a <see cref="DecisionVector"/>.
    /// </summary>
    /// <param name="plan">The legacy plan, or <see langword="null"/> when the legacy planner found no viable stream.</param>
    /// <returns>The equivalent, comparable decision vector.</returns>
    public static DecisionVector Project(PlaybackPlan? plan)
    {
        if (plan is null)
        {
            return new DecisionVector(
                IsViable: false,
                Method: null,
                VideoStreamIndex: null,
                AudioStreamIndex: null,
                SubtitleStreamIndex: null,
                TransformClasses: new HashSet<TransformClass>(),
                ReasonCategories: new HashSet<ReasonCategory>(),
                OutputContainer: null,
                OutputVideoCodec: null,
                OutputAudioCodec: null);
        }

        var method = MapMethod(plan.PlayMethod);
        var reasonCategories = FoldReasonCategories(plan.TranscodeReasons);
        var transformClasses = DeriveTransformClasses(method, reasonCategories);

        var streamInfo = plan.StreamInfo;

        return new DecisionVector(
            IsViable: true,
            Method: method,
            VideoStreamIndex: null,
            AudioStreamIndex: streamInfo?.AudioStreamIndex,
            SubtitleStreamIndex: streamInfo?.SubtitleStreamIndex,
            TransformClasses: transformClasses,
            ReasonCategories: reasonCategories,
            OutputContainer: streamInfo?.Container,
            OutputVideoCodec: streamInfo?.TargetVideoCodec.FirstOrDefault(),
            OutputAudioCodec: streamInfo?.TargetAudioCodec.FirstOrDefault());
    }

    private static NormalizedMethod MapMethod(PlayMethod method) => method switch
    {
        PlayMethod.DirectPlay => NormalizedMethod.DirectPlay,
        PlayMethod.DirectStream => NormalizedMethod.Remux,
        PlayMethod.Transcode => NormalizedMethod.Transcode,
        _ => NormalizedMethod.Transcode,
    };

    private static HashSet<ReasonCategory> FoldReasonCategories(TranscodeReason reasons)
    {
        var categories = new HashSet<ReasonCategory>();
        foreach (var flag in reasons.GetUniqueFlags())
        {
            var category = ReasonCategoryMap.MapByName(flag.ToString());
            if (category is not null)
            {
                categories.Add(category.Value);
            }
        }

        return categories;
    }

    /// <summary>
    /// Best-effort derivation of the transform set a legacy decision implies, since
    /// <c>TranscodeReason</c> records which walls were hit, not what the pipeline will do about
    /// them.
    /// </summary>
    private static HashSet<TransformClass> DeriveTransformClasses(NormalizedMethod method, IReadOnlySet<ReasonCategory> reasonCategories)
    {
        var transforms = new HashSet<TransformClass>();

        if (method == NormalizedMethod.Remux)
        {
            transforms.Add(TransformClass.Remux);
            return transforms;
        }

        if (method != NormalizedMethod.Transcode)
        {
            return transforms;
        }

        if (reasonCategories.Contains(ReasonCategory.VideoCodec) || reasonCategories.Contains(ReasonCategory.VideoRange) || reasonCategories.Contains(ReasonCategory.VideoDims))
        {
            transforms.Add(TransformClass.TranscodeVideo);
        }

        if (reasonCategories.Contains(ReasonCategory.AudioCodec) || reasonCategories.Contains(ReasonCategory.AudioChannels) || reasonCategories.Contains(ReasonCategory.AudioRate))
        {
            transforms.Add(TransformClass.TranscodeAudio);
        }

        if (reasonCategories.Contains(ReasonCategory.AudioChannels))
        {
            transforms.Add(TransformClass.Downmix);
        }

        if (reasonCategories.Contains(ReasonCategory.VideoRange))
        {
            transforms.Add(TransformClass.Tonemap);
        }

        if (reasonCategories.Contains(ReasonCategory.Subtitle))
        {
            transforms.Add(TransformClass.BurnInSubtitle);
        }

        if (reasonCategories.Contains(ReasonCategory.Container))
        {
            transforms.Add(TransformClass.Remux);
        }

        return transforms;
    }
}
