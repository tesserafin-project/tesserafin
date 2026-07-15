using System;
using System.Collections.Generic;
using System.Linq;
using Reefin.Controller.MediaEncoding;
using Reefin.Data.Enums;
using Reefin.Extensions;
using Reefin.Model.Dlna;
using Reefin.Model.Entities;
using Reefin.Model.Session;
using LegacySubtitleDeliveryMethod = Reefin.Model.Dlna.SubtitleDeliveryMethod;

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
                VideoStreamIndex: StreamSelection.Unknown,
                AudioStreamIndex: StreamSelection.Unknown,
                SubtitleStreamIndex: StreamSelection.Unknown,
                TransformClasses: new HashSet<TransformClass>(),
                ReasonCategories: new HashSet<ReasonCategory>(),
                OutputContainer: null,
                OutputVideoCodec: null,
                OutputAudioCodec: null,
                SelectedSource: null,
                OutputWidth: null,
                OutputHeight: null,
                OutputBitrate: null,
                OutputVideoRange: null,
                OutputAudioChannels: null,
                SubtitleDeliveryMode: null,
                OutputSubtitleFormat: null);
        }

        var method = MapMethod(plan.PlayMethod);
        var reasonCategories = FoldReasonCategories(plan.TranscodeReasons);
        var streamInfo = plan.StreamInfo;
        var transformClasses = DeriveTransformClasses(method, reasonCategories, streamInfo);

        // Legacy StreamInfo does not expose a video stream index cleanly (per PR98 spec): the
        // projector genuinely does not know, so this is Unknown, never None.
        var videoSelection = StreamSelection.Unknown;

        // A null AudioStreamIndex means "legacy fell back to the default audio track", not "no audio
        // was selected" - audio is mandatory for video playback. Unknown, not None.
        var audioSelection = streamInfo?.AudioStreamIndex is int audioIdx ? StreamSelection.Selected(audioIdx) : StreamSelection.Unknown;

        // Subtitles are opt-in: when StreamInfo is present but SubtitleStreamIndex is null, legacy
        // positively decided not to select one. This is the None case the PR98 comparator missed
        // (docs/pr93-compatibility-lab.md §4 gap 1).
        var subtitleSelection = ProjectSubtitleSelection(streamInfo);

        // "Output bitrate" is mapped to the target VIDEO bitrate specifically, not
        // StreamInfo.TargetTotalBitrate: the latter defaults either half to 0 when unknown
        // (`(TargetAudioBitrate ?? 0) + (TargetVideoBitrate ?? 0)`), which would fabricate a known
        // value out of an unknown one - exactly what this projector must not do.
        var outputBitrate = streamInfo?.TargetVideoBitrate;

        return new DecisionVector(
            IsViable: true,
            Method: method,
            VideoStreamIndex: videoSelection,
            AudioStreamIndex: audioSelection,
            SubtitleStreamIndex: subtitleSelection,
            TransformClasses: transformClasses,
            ReasonCategories: reasonCategories,
            OutputContainer: streamInfo?.Container,
            OutputVideoCodec: streamInfo?.TargetVideoCodec.FirstOrDefault(),
            OutputAudioCodec: streamInfo?.TargetAudioCodec.FirstOrDefault(),
            SelectedSource: streamInfo?.MediaSourceId,
            OutputWidth: streamInfo?.TargetWidth,
            OutputHeight: streamInfo?.TargetHeight,
            OutputBitrate: outputBitrate,
            OutputVideoRange: ProjectVideoRange(streamInfo),
            OutputAudioChannels: streamInfo?.TargetAudioChannels,
            SubtitleDeliveryMode: ProjectSubtitleDeliveryMode(streamInfo),
            OutputSubtitleFormat: subtitleSelection.IsSelected ? streamInfo?.SubtitleFormat : null);
    }

    private static StreamSelection ProjectSubtitleSelection(StreamInfo? streamInfo)
    {
        if (streamInfo is null)
        {
            // No StreamInfo at all: the projector has no basis to say "none", so Unknown.
            return StreamSelection.Unknown;
        }

        // Legacy StreamInfo uses -1 (not null) to mean "no subtitle selected": StreamBuilder leaves
        // SubtitleStreamIndex at -1 when nothing matches, mirroring StreamInfo's own treatment of -1
        // as no-selection. Only a non-negative index is a real selection; -1 projects to None, same as
        // null, so the shadow comparison does not report a false "subtitle selected only on legacy".
        return streamInfo.SubtitleStreamIndex is int idx && idx >= 0 ? StreamSelection.Selected(idx) : StreamSelection.None;
    }

    /// <summary>
    /// Best-effort detection of a real legacy subtitle text-format conversion (PR111c): legacy emits
    /// NO reason bit for a successful re-encode of a text subtitle stream (unlike v2's
    /// <c>ReasonCode.SubtitleFormatConverted</c>), so this compares the SOURCE subtitle stream's
    /// codec against the delivered <see cref="StreamInfo.SubtitleFormat"/> instead. <see langword="false"/>
    /// when no subtitle was selected, or when the delivery method is <see cref="LegacySubtitleDeliveryMethod.Encode"/>
    /// (burn-in re-encodes the video, not a subtitle format conversion for delivery).
    /// </summary>
    private static bool DetectedSubtitleConversion(StreamInfo? streamInfo)
    {
        if (streamInfo is null || streamInfo.SubtitleStreamIndex is null or < 0)
        {
            return false;
        }

        if (streamInfo.SubtitleDeliveryMethod == LegacySubtitleDeliveryMethod.Encode)
        {
            return false;
        }

        var sourceCodec = streamInfo.MediaSource?.MediaStreams
            ?.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == streamInfo.SubtitleStreamIndex)
            ?.Codec;

        return !string.Equals(NormalizeSubtitleFormat(sourceCodec), NormalizeSubtitleFormat(streamInfo.SubtitleFormat), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes the <c>webvtt</c>/<c>vtt</c> spelling alias so it never looks like a real
    /// conversion in <see cref="DetectedSubtitleConversion"/> - load-bearing on both operands: the
    /// direct-play control case has source codec <c>webvtt</c> but a delivered
    /// <see cref="StreamInfo.SubtitleFormat"/> of <c>vtt</c>, and without normalizing both sides the
    /// heuristic would misfire "converted" here and break the hard-asserted Equivalent gate for that
    /// case.
    /// </summary>
    private static string? NormalizeSubtitleFormat(string? format) =>
        string.Equals(format, "webvtt", StringComparison.OrdinalIgnoreCase) ? "vtt" : format;

    /// <summary>
    /// Best-effort detection of a real legacy HDR-to-SDR tonemap (PR111c): when the source video
    /// stream carries an HDR range (anything that is neither <see cref="VideoRangeType.SDR"/> nor
    /// <see cref="VideoRangeType.Unknown"/>) and legacy's chosen output range
    /// (<see cref="StreamInfo.TargetVideoRangeType"/>) is plain <see cref="VideoRangeType.SDR"/>, the
    /// pipeline is tonemapping - even though legacy may record NO
    /// <c>TranscodeReason.VideoRangeTypeNotSupported</c> bit for it (e.g. when the transcode is driven
    /// by an unsupported video codec instead, so the range folding never reaches
    /// <see cref="ReasonCategory.VideoRange"/>). Same shape as
    /// <see cref="DetectedSubtitleConversion"/>: derive the transform from output state, not from a
    /// reason bit legacy never set. Deliberately requires target == SDR exactly, so an HDR source
    /// that legacy keeps as an HDR/HLG fallback (for example the Dolby-Vision-on-Chrome case, target
    /// HLG) is NOT treated as a tonemap.
    /// </summary>
    private static bool DetectedTonemap(StreamInfo? streamInfo)
    {
        if (streamInfo is null)
        {
            return false;
        }

        var sourceRange = streamInfo.TargetVideoStream?.VideoRangeType;
        if (sourceRange is null or VideoRangeType.Unknown or VideoRangeType.SDR)
        {
            return false;
        }

        return streamInfo.TargetVideoRangeType == VideoRangeType.SDR;
    }

    /// <summary>
    /// Maps the legacy <see cref="Reefin.Data.Enums.VideoRangeType"/> to the same string vocabulary
    /// v2's <see cref="Reefin.Playback.Decision.OutputSpec.VideoRange"/> uses (for example
    /// <c>"SDR"</c>, <c>"HDR10"</c>), so <see cref="ShadowComparer"/> can compare them directly.
    /// <see cref="VideoRangeType.Unknown"/> maps to <see langword="null"/> (unknown), not a literal
    /// "Unknown" string, since it is not a real range value.
    /// </summary>
    private static string? ProjectVideoRange(StreamInfo? streamInfo)
    {
        if (streamInfo is null)
        {
            return null;
        }

        var rangeType = streamInfo.TargetVideoRangeType;
        return rangeType == VideoRangeType.Unknown ? null : rangeType.ToString();
    }

    /// <summary>
    /// Maps legacy's <see cref="LegacySubtitleDeliveryMethod"/> to the shared
    /// <see cref="Shadow.SubtitleDeliveryMode"/> vocabulary, mirroring the mapping
    /// <c>ClientCapabilitiesMapper.MapDeliveryMethod</c> uses for the same enum: <c>Encode</c> is
    /// legacy's name for burn-in and maps to <see cref="Shadow.SubtitleDeliveryMode.Burn"/>;
    /// <c>Drop</c> carries no equivalent domain concept and maps to <see langword="null"/>
    /// (unknown), same as that mapper's precedent.
    /// </summary>
    private static SubtitleDeliveryMode? ProjectSubtitleDeliveryMode(StreamInfo? streamInfo)
    {
        if (streamInfo is null)
        {
            return null;
        }

        // -1 means "no subtitle selected" (see ProjectSubtitleSelection): treat it as None regardless
        // of the delivery method left on the StreamInfo, which is otherwise its enum default (Encode)
        // and would be mis-projected as Burn.
        if (streamInfo.SubtitleStreamIndex is null or < 0)
        {
            return SubtitleDeliveryMode.None;
        }

        return streamInfo.SubtitleDeliveryMethod switch
        {
            LegacySubtitleDeliveryMethod.Encode => SubtitleDeliveryMode.Burn,
            LegacySubtitleDeliveryMethod.Embed => SubtitleDeliveryMode.Embed,
            LegacySubtitleDeliveryMethod.External => SubtitleDeliveryMode.External,
            LegacySubtitleDeliveryMethod.Hls => SubtitleDeliveryMode.Hls,
            _ => null,
        };
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
    private static HashSet<TransformClass> DeriveTransformClasses(NormalizedMethod method, IReadOnlySet<ReasonCategory> reasonCategories, StreamInfo? streamInfo)
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

        // PR111c: an HDR source transcoded to an SDR target is tonemapped even when the transcode was
        // forced by an unsupported video codec (ReasonCategory.VideoCodec) rather than the range
        // itself, so legacy records no VideoRangeTypeNotSupported bit and the ReasonCategory.VideoRange
        // branch above never fires. Derived from output state (see DetectedTonemap), independently of
        // the reason categories, and only alongside an actual video transcode - mirrors the
        // ConvertSubtitle derivation below. TranscodeVideo is already present here whenever the video
        // is re-encoded, so a null-range source or an HDR-preserving fallback (target != SDR) adds
        // nothing.
        if (transforms.Contains(TransformClass.TranscodeVideo) && DetectedTonemap(streamInfo))
        {
            transforms.Add(TransformClass.Tonemap);
        }

        if (reasonCategories.Contains(ReasonCategory.Subtitle))
        {
            transforms.Add(TransformClass.BurnInSubtitle);
        }

        // PR111c: a real subtitle text-format conversion carries no ReasonCategory.Subtitle bit
        // (legacy emits no reason at all for a successful re-encode - see
        // DetectedSubtitleConversion), so this is derived independently of the reasonCategories
        // check above. Deliberately gated to method == Transcode only, in symmetry with v2 (which
        // can never emit a ConvertSubtitle transform on DirectPlay either - PlaybackEngine only
        // evaluates the subtitle conversion candidate as part of building a transcode/remux
        // candidate's transform list).
        if (method == NormalizedMethod.Transcode && DetectedSubtitleConversion(streamInfo))
        {
            transforms.Add(TransformClass.ConvertSubtitle);
        }

        if (reasonCategories.Contains(ReasonCategory.Container))
        {
            transforms.Add(TransformClass.Remux);
        }

        return transforms;
    }
}
