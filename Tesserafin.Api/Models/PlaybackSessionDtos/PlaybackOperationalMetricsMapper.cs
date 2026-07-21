using System;
using System.Collections.Generic;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// PR115d: projects <see cref="PlaybackOperationalMetricsSnapshot"/> plus the live stop-threshold
/// guard state into the wire-facing <see cref="PlaybackOperationalMetricsResponse"/> - the same
/// snapshot-to-DTO mapper shape as <see cref="PlaybackDiagnosticDetailMapper"/>.
/// </summary>
public static class PlaybackOperationalMetricsMapper
{
    /// <summary>
    /// Maps a metrics snapshot and the current guard state to the response DTO.
    /// </summary>
    /// <param name="snapshot">The current cumulative counters.</param>
    /// <param name="stopThresholdOptions">The live stop-threshold configuration, for <see cref="PlaybackOperationalMetricsResponse.StopThresholdGuardEnabled"/>.</param>
    /// <param name="stopThresholdGuardTripped">The guard's current evaluation - the caller is responsible for calling <see cref="PlaybackStopThresholdGuard.Evaluate"/> once and passing its result here, so this mapper stays a pure projection.</param>
    /// <returns>The response DTO.</returns>
    public static PlaybackOperationalMetricsResponse Map(
        PlaybackOperationalMetricsSnapshot snapshot,
        PlaybackStopThresholdOptions stopThresholdOptions,
        bool stopThresholdGuardTripped)
    {
        var fallbackReasonCounts = new Dictionary<string, long>();
        foreach (var kvp in snapshot.FallbackReasonCounts)
        {
            fallbackReasonCounts[kvp.Key.ToString()] = kvp.Value;
        }

        return new PlaybackOperationalMetricsResponse(
            snapshot.ServedByV2Count,
            snapshot.ServedByLegacyCount,
            fallbackReasonCounts,
            snapshot.AdapterAttempts,
            snapshot.AdapterErrorRate,
            snapshot.TranscodeStartAttemptsV2,
            snapshot.TranscodeStartFailuresV2,
            snapshot.TranscodeStartFailureRate,
            stopThresholdOptions.Enabled,
            stopThresholdGuardTripped,
            DateTimeOffset.UtcNow);
    }
}
