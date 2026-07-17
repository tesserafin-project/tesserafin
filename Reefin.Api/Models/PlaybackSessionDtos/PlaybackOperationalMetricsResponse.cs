using System;
using System.Collections.Generic;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// PR115d: the admin-only operational gate surface for the v2 canary - the response body of
/// <c>GET System/PlaybackDiagnostics/Metrics</c>. Cumulative, cross-session counters
/// (<see cref="Reefin.MediaEncoding.Playback.PlaybackOperationalMetrics"/>) plus the live
/// stop-threshold guard state (<see cref="Reefin.MediaEncoding.Playback.PlaybackStopThresholdGuard"/>) -
/// deliberately counters only, never session identifiers/paths/URLs, so this can stay a cheap,
/// dependency-free projection like the sibling <see cref="PlaybackSessionListItem"/>.
/// </summary>
/// <param name="ServedByV2Count">Live requests actually served from the v2 execution plan.</param>
/// <param name="ServedByLegacyCount">Live requests that fell back to legacy, across every reason.</param>
/// <param name="FallbackReasonCounts">
/// Count of fallback-to-legacy requests, per typed <c>PlaybackLiveFallbackReason</c>, keyed by the
/// enum member's name (not its numeric value) for a stable, readable JSON shape.
/// </param>
/// <param name="AdapterAttempts">
/// Live requests that reached the adapter at all - served-by-v2 plus <c>AdapterError</c> - the
/// denominator <see cref="AdapterErrorRate"/> is computed over.
/// </param>
/// <param name="AdapterErrorRate">Fraction of <see cref="AdapterAttempts"/> that ended in <c>AdapterError</c>.</param>
/// <param name="TranscodeStartAttemptsV2">Ffmpeg transcode starts observed for v2-served sessions.</param>
/// <param name="TranscodeStartFailuresV2">Of those, how many never actually started.</param>
/// <param name="TranscodeStartFailureRate">Fraction of <see cref="TranscodeStartAttemptsV2"/> that failed to start.</param>
/// <param name="StopThresholdGuardEnabled">Whether the guard is enabled in the live configuration right now.</param>
/// <param name="StopThresholdGuardTripped">
/// Whether the guard is forcing legacy for every live request right now - see
/// <see cref="Reefin.MediaEncoding.Playback.PlaybackStopThresholdGuard"/>'s remarks: sticky in
/// practice once tripped, cleared only by an operator config change.
/// </param>
/// <param name="GeneratedAt">When this snapshot was taken.</param>
public sealed record PlaybackOperationalMetricsResponse(
    long ServedByV2Count,
    long ServedByLegacyCount,
    IReadOnlyDictionary<string, long> FallbackReasonCounts,
    long AdapterAttempts,
    double AdapterErrorRate,
    long TranscodeStartAttemptsV2,
    long TranscodeStartFailuresV2,
    double TranscodeStartFailureRate,
    bool StopThresholdGuardEnabled,
    bool StopThresholdGuardTripped,
    DateTimeOffset GeneratedAt);
