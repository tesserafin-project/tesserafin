using System.Collections.Generic;

namespace Reefin.Playback.Shadow;

/// <summary>
/// A playback decision reduced to a comparable vector of categories, per
/// docs/pr93-compatibility-lab.md §4: method, selected stream indices, the set of transform
/// classes, the set of reason categories, and the output container/codecs. Produced from either
/// side by <see cref="LegacyDecisionProjector"/> or <see cref="V2DecisionProjector"/>, and compared
/// by <see cref="ShadowComparer"/> — never by raw equality of the underlying
/// <c>Reefin.Model.Dlna.StreamInfo</c> / <c>Reefin.Playback.Decision.PlaybackDecision</c> types.
/// </summary>
/// <param name="IsViable">Whether a playable plan was produced.</param>
/// <param name="Method">The normalized method chosen, or <see langword="null"/> when <paramref name="IsViable"/> is <see langword="false"/>.</param>
/// <param name="VideoStreamIndex">The selected video stream index, or <see langword="null"/> if not known/applicable.</param>
/// <param name="AudioStreamIndex">The selected audio stream index, or <see langword="null"/> if not known/applicable.</param>
/// <param name="SubtitleStreamIndex">The selected subtitle stream index, or <see langword="null"/> if not known/applicable.</param>
/// <param name="TransformClasses">The set of pipeline transformations this decision implies.</param>
/// <param name="ReasonCategories">The set of reason categories that led to this decision.</param>
/// <param name="OutputContainer">The output container, or <see langword="null"/> if not known/applicable.</param>
/// <param name="OutputVideoCodec">The output video codec, or <see langword="null"/> if not known/applicable.</param>
/// <param name="OutputAudioCodec">The output audio codec, or <see langword="null"/> if not known/applicable.</param>
public sealed record DecisionVector(
    bool IsViable,
    NormalizedMethod? Method,
    int? VideoStreamIndex,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex,
    IReadOnlySet<TransformClass> TransformClasses,
    IReadOnlySet<ReasonCategory> ReasonCategories,
    string? OutputContainer,
    string? OutputVideoCodec,
    string? OutputAudioCodec);
