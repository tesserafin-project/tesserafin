using System;
using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Request body for creating (or replacing, when <see cref="PlaySessionId"/> matches an existing
/// session) a playback session via the point-1 v2 protocol.
/// </summary>
/// <param name="ItemId">The item to plan playback for.</param>
/// <param name="UserId">The requesting user.</param>
/// <param name="Capabilities">What the requesting client can decode and wants produced when transcoding - see <see cref="PlaybackPlanRequestBase"/>.</param>
/// <param name="Constraints">The playback method/bitrate/subtitle preferences and limits for this request - see <see cref="PlaybackPlanRequestBase"/>.</param>
/// <param name="MediaSourceId">Optional. A specific media source id, if playing an alternate version.</param>
/// <param name="PlaySessionId">
/// Optional. The client-facing play session id. At most one session is kept per play session id:
/// creating with the same id again replaces that session's plan and request.
/// </param>
/// <param name="PlaybackAttemptId">Issue #43. Optional, opaque, stable across the whole playback attempt - see <see cref="PlaybackPlanRequestBase"/>.</param>
public sealed record CreatePlaybackSessionRequest(
    Guid ItemId,
    Guid UserId,
    ClientCapabilities Capabilities,
    PlaybackConstraints Constraints,
    string? MediaSourceId = null,
    string? PlaySessionId = null,
    string? PlaybackAttemptId = null)
    : PlaybackPlanRequestBase(ItemId, UserId, Capabilities, Constraints, MediaSourceId, PlaybackAttemptId);
