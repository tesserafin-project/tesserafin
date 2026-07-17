using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using Reefin.Controller.Configuration;
using Reefin.Controller.MediaEncoding;
using Reefin.Data.Enums;
using Reefin.Model.Configuration;
using Reefin.Model.Dlna;
using Reefin.Model.Dto;
using Reefin.Model.Entities;
using Reefin.Playback.Dlna;
using Reefin.Playback.Execution;

namespace Reefin.MediaEncoding.Playback;

/// <inheritdoc cref="IPlaybackLiveStreamResolver"/>
public sealed class PlaybackLiveStreamResolver : IPlaybackLiveStreamResolver
{
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IPlaybackExecutionPlanResolver _executionPlanResolver;
    private readonly IPlaybackLiveWiringDiagnosticsStore _liveWiringDiagnosticsStore;
    private readonly PlaybackOperationalMetrics _operationalMetrics;
    private readonly PlaybackStopThresholdGuard _stopThresholdGuard;
    private readonly ILogger<PlaybackLiveStreamResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackLiveStreamResolver"/> class.
    /// </summary>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface - read live on every call (kill switch immediacy).</param>
    /// <param name="executionPlanResolver">Instance of the <see cref="IPlaybackExecutionPlanResolver"/> interface - the sole resolution point over <see cref="IV2PlanStore"/>.</param>
    /// <param name="liveWiringDiagnosticsStore">Instance of the <see cref="IPlaybackLiveWiringDiagnosticsStore"/> interface.</param>
    /// <param name="operationalMetrics">The cumulative served-by-v2/fallback-by-reason counters.</param>
    /// <param name="stopThresholdGuard">The operational stop-threshold guard.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PlaybackLiveStreamResolver}"/> interface.</param>
    public PlaybackLiveStreamResolver(
        IServerConfigurationManager serverConfigurationManager,
        IPlaybackExecutionPlanResolver executionPlanResolver,
        IPlaybackLiveWiringDiagnosticsStore liveWiringDiagnosticsStore,
        PlaybackOperationalMetrics operationalMetrics,
        PlaybackStopThresholdGuard stopThresholdGuard,
        ILogger<PlaybackLiveStreamResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(serverConfigurationManager);
        ArgumentNullException.ThrowIfNull(executionPlanResolver);
        ArgumentNullException.ThrowIfNull(liveWiringDiagnosticsStore);
        ArgumentNullException.ThrowIfNull(operationalMetrics);
        ArgumentNullException.ThrowIfNull(stopThresholdGuard);
        ArgumentNullException.ThrowIfNull(logger);

        _serverConfigurationManager = serverConfigurationManager;
        _executionPlanResolver = executionPlanResolver;
        _liveWiringDiagnosticsStore = liveWiringDiagnosticsStore;
        _operationalMetrics = operationalMetrics;
        _stopThresholdGuard = stopThresholdGuard;
        _logger = logger;
    }

    /// <inheritdoc/>
    public StreamInfo Resolve(
        PlaybackSessionId sessionId,
        StreamInfo legacyStreamInfo,
        MediaSourceInfo mediaSource,
        DeviceProfile profile,
        Guid itemId,
        string? deviceId,
        string playSessionId,
        long startTimeTicks,
        bool alwaysBurnInSubtitleWhenTranscoding)
    {
        var decidedAt = DateTimeOffset.UtcNow;

        // Kill switch: an operator-controlled, immediate off-switch, independent of cohort
        // membership - forces legacy for every session while the effective mode does not authorize
        // serving v2 live. Reads live server configuration on every call, the same mechanism
        // ShadowPlaybackSessionPlanner's own optionsAccessor already relies on for "no restart
        // required" - flipping PlaybackShadow.Mode back to Legacy/Shadow takes effect on the very
        // next request. Checked even before resolving a plan: belt-and-suspenders against a
        // hypothetically stale IV2PlanStore record outliving a mode change, not just the ordinary
        // "session created before the switch flipped" case IV2PlanStore's own attach-or-evict
        // discipline in PlaybackSessionManager already handles.
        var effectiveMode = _serverConfigurationManager.Configuration.PlaybackShadow.GetEffectiveMode();
        if (effectiveMode is not (PlaybackEngineMode.Canary or PlaybackEngineMode.V2))
        {
            return FallbackToLegacy(sessionId, legacyStreamInfo, PlaybackLiveFallbackReason.KillSwitch, decidedAt);
        }

        // PR115d: the operational stop-threshold guard - consulted right after the kill switch (an
        // operator-forced override always wins first) and before resolving a plan, so a tripped guard
        // never pays the cost of resolving/adapting a plan it is about to discard anyway. See
        // PlaybackStopThresholdGuard's remarks for the full trip/log/reset semantics.
        if (_stopThresholdGuard.Evaluate())
        {
            return FallbackToLegacy(sessionId, legacyStreamInfo, PlaybackLiveFallbackReason.StopThresholdTripped, decidedAt);
        }

        var resolution = _executionPlanResolver.Resolve(sessionId, out var plan);
        if (resolution != PlaybackExecutionPlanResolution.Resolved || plan is null)
        {
            var reason = resolution == PlaybackExecutionPlanResolution.PlanNotExecutable
                ? PlaybackLiveFallbackReason.PlanNotExecutable
                : PlaybackLiveFallbackReason.NoAuthoritativeRecord;
            return FallbackToLegacy(sessionId, legacyStreamInfo, reason, decidedAt);
        }

        // Strict SourceId verification: the plan must never be applied to a different source than
        // the one v2 actually selected. Checked explicitly here - not left to the adapter's own
        // ArgumentException - so a mismatch is a typed, observable fallback rather than an unhandled
        // exception reaching the caller. Same comparison (Ordinal) the adapter itself uses.
        if (!string.Equals(mediaSource.Id, plan.SourceId, StringComparison.Ordinal))
        {
            return FallbackToLegacy(sessionId, legacyStreamInfo, PlaybackLiveFallbackReason.SourceIdMismatch, decidedAt);
        }

        // Mandatory exclusion (PR115b design doc, "Constat de sortie PR115b" #2): a Dolby
        // Vision/HDR source whose codec appears in legacy's own candidate codec CSV is the class of
        // session EncodingHelper.CanStreamCopyVideo can stream-copy incompatibly instead of
        // transcoding - a pre-existing legacy pipeline behavior, not yet investigated for the v2 live
        // path. Excluded unconditionally until that investigation happens; not a rollout policy knob.
        if (IsDolbyVisionExcluded(plan, mediaSource, legacyStreamInfo.VideoCodecs))
        {
            return FallbackToLegacy(sessionId, legacyStreamInfo, PlaybackLiveFallbackReason.DolbyVisionExclusion, decidedAt);
        }

        try
        {
            var context = new PlaybackExecutionContext(
                itemId,
                playSessionId,
                deviceId,
                profile.Id?.ToString("N", CultureInfo.InvariantCulture),
                startTimeTicks,
                alwaysBurnInSubtitleWhenTranscoding);

            var v2StreamInfo = PlaybackExecutionPlanAdapter.ToStreamInfo(plan, context, mediaSource, profile);
            _liveWiringDiagnosticsStore.Record(sessionId, PlaybackLiveWiringOutcome.Served(decidedAt));
            _operationalMetrics.RecordServed();
            _logger.LogInformation("Playback session {SessionId} served from the v2 execution plan (PR115c canary).", sessionId);
            return v2StreamInfo;
        }
#pragma warning disable CA1031 // Do not catch general exception types - v2 must never break the live path, same discipline as ShadowPlaybackSessionPlanner.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Playback session {SessionId}: v2 execution plan adapter threw; falling back to legacy.", sessionId);
            return FallbackToLegacy(sessionId, legacyStreamInfo, PlaybackLiveFallbackReason.AdapterError, decidedAt);
        }
    }

    private StreamInfo FallbackToLegacy(PlaybackSessionId sessionId, StreamInfo legacyStreamInfo, PlaybackLiveFallbackReason reason, DateTimeOffset decidedAt)
    {
        _liveWiringDiagnosticsStore.Record(sessionId, PlaybackLiveWiringOutcome.Fallback(reason, decidedAt));
        _operationalMetrics.RecordFallback(reason);
        _logger.LogInformation("Playback session {SessionId} served from legacy (PR115c fallback reason: {Reason}).", sessionId, reason);
        return legacyStreamInfo;
    }

    /// <summary>
    /// PR115b design doc, "Constat de sortie PR115b" #2: excludes a Dolby Vision/HDR source whose
    /// codec appears in legacy's own candidate codec CSV (<paramref name="legacyVideoCodecsCsv"/>) -
    /// broader than a plain <see cref="VideoRange.HDR"/> check because <see cref="VideoRangeType.DOVIWithSDR"/>
    /// (Dolby Vision profile 8.2, base layer SDR) reports <see cref="VideoRange.SDR"/> despite still
    /// being Dolby Vision - the exclusion must catch that case too, not just the ones that already
    /// read as HDR.
    /// </summary>
    private static bool IsDolbyVisionExcluded(PlaybackExecutionPlan plan, MediaSourceInfo mediaSource, IReadOnlyList<string> legacyVideoCodecsCsv)
    {
        if (plan.VideoStreamIndex is not int videoIndex)
        {
            return false;
        }

        var videoStream = mediaSource.GetMediaStream(MediaStreamType.Video, videoIndex);
        if (videoStream is null || string.IsNullOrEmpty(videoStream.Codec))
        {
            return false;
        }

        var isDolbyVisionOrHdr = videoStream.VideoRange == VideoRange.HDR || IsDolbyVisionRangeType(videoStream.VideoRangeType);
        return isDolbyVisionOrHdr && legacyVideoCodecsCsv.Contains(videoStream.Codec, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDolbyVisionRangeType(VideoRangeType type) => type is VideoRangeType.DOVI
        or VideoRangeType.DOVIWithHDR10
        or VideoRangeType.DOVIWithHLG
        or VideoRangeType.DOVIWithSDR
        or VideoRangeType.DOVIWithEL
        or VideoRangeType.DOVIWithHDR10Plus
        or VideoRangeType.DOVIWithELHDR10Plus
        or VideoRangeType.DOVIInvalid;
}
