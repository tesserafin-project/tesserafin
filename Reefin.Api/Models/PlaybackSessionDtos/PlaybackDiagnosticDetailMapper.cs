using System.Collections.Generic;
using System.Linq;
using Reefin.Controller.MediaEncoding;
using Reefin.MediaEncoding.Playback;
using Reefin.Playback.Decision;
using Reefin.Playback.Shadow;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Maps a tracked <see cref="PlaybackSession"/> plus its (optional) retained
/// <see cref="ShadowDiagnosticRecord"/> into a <see cref="PlaybackDiagnosticDetail"/>
/// (docs/pr92-design-playback-api-and-diagnostics.md §4.3, PR113).
/// </summary>
public static class PlaybackDiagnosticDetailMapper
{
    /// <summary>
    /// Maps the shared <see cref="ReasonCategory"/> vocabulary (folded, many-to-one, from either
    /// side's raw reason names - see the internal <c>Reefin.Playback.Shadow.ReasonCategoryMap</c>)
    /// back onto one representative <see cref="ReasonCode"/> per category. This is necessarily lossy:
    /// <see cref="DecisionVector"/>
    /// retains only the folded category set, never the original legacy <c>TranscodeReason</c> flags,
    /// so (for example) a category folded from several video-codec-shaped reasons always surfaces as
    /// <see cref="ReasonCode.VideoCodecNotSupported"/> here, even if the real legacy reason was, say,
    /// <see cref="ReasonCode.VideoProfileNotSupported"/>. Good enough for an admin diagnostic
    /// (docs/pr93-compatibility-lab.md §4.1's own comparison granularity), not a promise of the exact
    /// original reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<ReasonCategory, ReasonCode> CategoryToRepresentativeCode = new Dictionary<ReasonCategory, ReasonCode>
    {
        [ReasonCategory.Container] = ReasonCode.ContainerNotSupported,
        [ReasonCategory.VideoCodec] = ReasonCode.VideoCodecNotSupported,
        [ReasonCategory.VideoRange] = ReasonCode.VideoRangeTypeNotSupported,
        [ReasonCategory.VideoDims] = ReasonCode.VideoResolutionNotSupported,
        [ReasonCategory.AudioCodec] = ReasonCode.AudioCodecNotSupported,
        [ReasonCategory.AudioChannels] = ReasonCode.AudioChannelsNotSupported,
        [ReasonCategory.AudioRate] = ReasonCode.AudioSampleRateNotSupported,
        [ReasonCategory.Bitrate] = ReasonCode.ContainerBitrateExceedsLimit,
        [ReasonCategory.Subtitle] = ReasonCode.SubtitleCodecNotSupported,
        [ReasonCategory.StreamCount] = ReasonCode.StreamCountExceedsLimit,
        [ReasonCategory.Error] = ReasonCode.DirectPlayError,
    };

    /// <summary>
    /// Maps a session and its (optional) retained diagnostic into the admin detail projection.
    /// </summary>
    /// <param name="session">The session to map.</param>
    /// <param name="diagnostic">
    /// The retained shadow diagnostic for this session, or <see langword="null"/> when none was
    /// retained (shadow mode disabled is the common, default case) - in which case every
    /// v2-sourced field on the result is <see langword="null"/>/empty and only the base fields
    /// (still legacy-sourced, per <see cref="PlaybackSessionResponseMapper"/>) are populated.
    /// </param>
    /// <returns>The mapped detail.</returns>
    public static PlaybackDiagnosticDetail Map(PlaybackSession session, ShadowDiagnosticRecord? diagnostic)
    {
        // Reuses the existing mapper for every base field rather than re-deriving Method/Output/
        // Transforms/Reasons by hand - this DTO only adds the v2-sourced fields on top.
        var baseResponse = PlaybackSessionResponseMapper.Map(session);

        return new PlaybackDiagnosticDetail(
            baseResponse.Id,
            baseResponse.Kind,
            baseResponse.DecisionVersion,
            baseResponse.Method,
            baseResponse.Output,
            baseResponse.SelectedStreams,
            baseResponse.Transforms,
            baseResponse.Reasons,
            baseResponse.CreatedAt,
            baseResponse.UpdatedAt,
            diagnostic?.Context,
            diagnostic?.Capabilities,
            diagnostic?.Sources,
            diagnostic?.Decision.Reasoning,
            diagnostic is null ? null : MapComparison(diagnostic),
            BuildTimeline(session));
    }

    private static DiagnosticComparison MapComparison(ShadowDiagnosticRecord diagnostic)
    {
        var legacyMethod = MapLegacyMethod(diagnostic.LegacyVector.Method);
        var legacyReasons = diagnostic.LegacyVector.ReasonCategories
            .Select(category => CategoryToRepresentativeCode.TryGetValue(category, out var code) ? code : (ReasonCode?)null)
            .Where(code => code is not null)
            .Select(code => code!.Value)
            .ToList();

        return new DiagnosticComparison(legacyMethod, legacyReasons, diagnostic.Divergence.Class);
    }

    /// <summary>
    /// Maps the shared <see cref="NormalizedMethod"/> vocabulary onto the real
    /// <see cref="PlaybackMethod"/> the response contract exposes. A <see langword="null"/>
    /// <paramref name="method"/> (legacy found no viable plan at all) falls back to
    /// <see cref="PlaybackMethod.Transcode"/>, mirroring <c>PlaybackSessionResponseMapper.MapMethod</c>'s
    /// own default arm - there is no "no method" value in the stable contract.
    /// </summary>
    private static PlaybackMethod MapLegacyMethod(NormalizedMethod? method) => method switch
    {
        NormalizedMethod.DirectPlay => PlaybackMethod.DirectPlay,
        NormalizedMethod.Remux => PlaybackMethod.Remux,
        NormalizedMethod.Transcode => PlaybackMethod.Transcode,
        _ => PlaybackMethod.Transcode,
    };

    /// <summary>
    /// Builds the lifecycle timeline for this slice: <c>Created</c>/<c>Updated</c> only, straight off
    /// the session record. "ffmpeg launched" and "playback started" have no retained signal yet
    /// (deferred - docs/pr92-design-playback-api-and-diagnostics.md §4.3).
    /// </summary>
    private static IReadOnlyList<DiagnosticTimelineEntry> BuildTimeline(PlaybackSession session) =>
    [
        new DiagnosticTimelineEntry("Created", session.CreatedAt),
        new DiagnosticTimelineEntry("Updated", session.UpdatedAt),
    ];
}
