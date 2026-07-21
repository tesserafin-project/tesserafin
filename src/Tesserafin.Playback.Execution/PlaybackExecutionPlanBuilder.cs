using System;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Execution;

/// <summary>
/// Builds a <see cref="PlaybackExecutionPlan"/> from a <c>Tesserafin.Playback.Engine.PlaybackEngine</c>
/// <see cref="PlaybackDecision"/>. A pure projection (PR114a): every field is copied verbatim from
/// the decision, never re-derived, never re-decided, never consults capabilities or constraints, and
/// never falls back to a guessed value. When the decision is not viable, or is viable but missing a
/// field execution genuinely requires, this type REFUSES - it never invents a plan the decision did
/// not actually make. See <see cref="TryBuild"/>/<see cref="Build"/> for the two ways to observe a
/// refusal, and <see cref="PlaybackExecutionPlanRefusalReason"/> for the refusal catalog.
/// </summary>
public static class PlaybackExecutionPlanBuilder
{
    /// <summary>
    /// Attempts to build a <see cref="PlaybackExecutionPlan"/> from <paramref name="decision"/>,
    /// without throwing. The non-throwing entry point: intended for callers - such as a session-scoped
    /// resolver - for which "no plan available" is an ordinary, expected outcome rather than an
    /// exceptional one.
    /// </summary>
    /// <param name="decision">The v2 engine's decision to project.</param>
    /// <param name="plan">The built plan, or <see langword="null"/> if the decision was refused.</param>
    /// <param name="refusalReason">The reason the decision was refused, or <see langword="null"/> if a plan was built.</param>
    /// <returns><see langword="true"/> if a plan was built; <see langword="false"/> if the decision was refused.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="decision"/> is <see langword="null"/>.</exception>
    public static bool TryBuild(
        PlaybackDecision decision,
        out PlaybackExecutionPlan? plan,
        out PlaybackExecutionPlanRefusalReason? refusalReason)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.IsViable)
        {
            plan = null;
            refusalReason = PlaybackExecutionPlanRefusalReason.NotViable;
            return false;
        }

        if (decision.SelectedStreams.Video is null && decision.SelectedStreams.Audio is null)
        {
            plan = null;
            refusalReason = PlaybackExecutionPlanRefusalReason.NoStreamsSelected;
            return false;
        }

        if (string.IsNullOrEmpty(decision.Output.Container))
        {
            plan = null;
            refusalReason = PlaybackExecutionPlanRefusalReason.MissingOutputContainer;
            return false;
        }

        var subtitle = decision.SelectedStreams.Subtitle;

        plan = new PlaybackExecutionPlan(
            Method: decision.Method,
            SourceId: decision.SelectedSource,
            Container: decision.Output.Container,
            Protocol: decision.Output.Protocol,
            VideoStreamIndex: decision.SelectedStreams.Video,
            VideoCodec: decision.Output.VideoCodec,
            VideoBitrate: decision.Output.VideoBitrate,
            Resolution: decision.Output.Resolution,
            VideoRange: decision.Output.VideoRange,
            AudioStreamIndex: decision.SelectedStreams.Audio,
            AudioCodec: decision.Output.AudioCodec,
            AudioBitrate: decision.Output.AudioBitrate,
            AudioChannels: decision.Output.AudioChannels,
            TotalBitrate: decision.Output.TotalBitrate,
            SubtitleStreamIndex: subtitle?.Index,
            SubtitleDelivery: subtitle?.Delivery,
            SubtitleFormat: decision.Output.SubtitleFormat,
            Transforms: decision.Transforms);
        refusalReason = null;
        return true;
    }

    /// <summary>
    /// Builds a <see cref="PlaybackExecutionPlan"/> from <paramref name="decision"/>, throwing when
    /// it is refused. The throwing entry point: intended for callers for which an unbuildable
    /// decision is a genuine error to surface (for example, a test asserting a decision must produce
    /// a plan). See <see cref="TryBuild"/> for a non-throwing alternative.
    /// </summary>
    /// <param name="decision">The v2 engine's decision to project.</param>
    /// <returns>The built plan.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlaybackExecutionPlanRefusedException">Thrown when the decision is refused; see <see cref="PlaybackExecutionPlanRefusalReason"/>.</exception>
    public static PlaybackExecutionPlan Build(PlaybackDecision decision)
    {
        if (TryBuild(decision, out var plan, out var refusalReason))
        {
            return plan!;
        }

        var message = refusalReason switch
        {
            PlaybackExecutionPlanRefusalReason.NotViable =>
                "The decision is not viable: the engine found no playable plan for this request.",
            PlaybackExecutionPlanRefusalReason.NoStreamsSelected =>
                "The decision selects neither a video nor an audio stream: there is nothing to execute.",
            PlaybackExecutionPlanRefusalReason.MissingOutputContainer =>
                "The decision's Output.Container is missing: the target container is required to execute a plan.",
            _ => "The decision was refused for an unspecified reason.",
        };

        throw new PlaybackExecutionPlanRefusedException(refusalReason!.Value, message);
    }
}
