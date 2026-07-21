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
    /// <param name="events">
    /// PR113b: the real, observed lifecycle events retained for this session (ffmpeg launched,
    /// playback started/stopped), independent of <paramref name="diagnostic"/> - or
    /// <see langword="null"/>/empty when none were observed. Defaults to <see langword="null"/> so
    /// every pre-PR113b (2-arg) call site keeps compiling and yields exactly the
    /// <c>Created</c>/<c>Updated</c> timeline it always did.
    /// </param>
    /// <param name="liveWiring">
    /// PR115c: the retained live-wiring decision for this session, or <see langword="null"/> when
    /// none has been retained - independent of <paramref name="diagnostic"/>, see
    /// <see cref="PlaybackDiagnosticDetail.LiveWiring"/>. Defaults to <see langword="null"/> so every
    /// pre-PR115c call site keeps compiling and yields exactly the same result it always did.
    /// </param>
    /// <returns>The mapped detail.</returns>
    public static PlaybackDiagnosticDetail Map(
        PlaybackSession session,
        ShadowDiagnosticRecord? diagnostic,
        IReadOnlyList<PlaybackLifecycleEvent>? events = null,
        PlaybackLiveWiringOutcome? liveWiring = null)
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
            BuildTimeline(session, events),
            liveWiring,
            session.PlaybackAttemptId,
            // Issue #75: passed straight through from the retained record - this mapper computes
            // nothing here and has nothing to filter, because the type it forwards is structurally
            // incapable of carrying a client-supplied value.
            diagnostic?.ContractMapping);
    }

    private static DiagnosticComparison MapComparison(ShadowDiagnosticRecord diagnostic)
    {
        var legacyMethod = MapLegacyMethod(diagnostic.LegacyVector.Method);
        var legacyReasons = diagnostic.LegacyVector.ReasonCategories
            .Select(category => CategoryToRepresentativeCode.TryGetValue(category, out var code) ? code : (ReasonCode?)null)
            .Where(code => code is not null)
            .Select(code => code!.Value)
            .ToList();

        return new DiagnosticComparison(
            legacyMethod,
            legacyReasons,
            diagnostic.Divergence.Class,
            diagnostic.Divergence.Summary,
            diagnostic.Decision.Method,
            diagnostic.Decision.Output,
            diagnostic.Decision.SelectedStreams,
            diagnostic.Decision.Transforms);
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
    /// Builds the lifecycle timeline for this session: <c>Created</c>/<c>Updated</c> straight off
    /// the session record, followed (PR113b) by every real, observed <paramref name="events"/> in
    /// the order they were recorded - ffmpeg launched, playback started, playback stopped.
    /// Deliberately never fabricates an entry for a stage that was not actually observed: an event
    /// missing from <paramref name="events"/> is simply absent from the result, not defaulted to
    /// some approximated timestamp.
    /// </summary>
    private static IReadOnlyList<DiagnosticTimelineEntry> BuildTimeline(PlaybackSession session, IReadOnlyList<PlaybackLifecycleEvent>? events)
    {
        var timeline = new List<DiagnosticTimelineEntry>(2 + (events?.Count ?? 0))
        {
            new("Created", session.CreatedAt),
            new("Updated", session.UpdatedAt),
        };

        if (events is not null)
        {
            foreach (var lifecycleEvent in events)
            {
                // Issue #42: carried through verbatim, null included — never substituted with the
                // request id of the admin call currently reading the timeline, which would be a
                // different request entirely and would make the field actively misleading.
                timeline.Add(new DiagnosticTimelineEntry(lifecycleEvent.Stage, lifecycleEvent.At, lifecycleEvent.RequestId));
            }
        }

        return timeline;
    }
}
