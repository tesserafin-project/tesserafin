using System;
using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Request body for fully re-planning an existing playback session (PR92 §3's <c>PUT</c> decision
/// v1). Distinct from <see cref="CreatePlaybackSessionRequest"/>: a replace targets the session named
/// in the route, so there is no <c>PlaySessionId</c> field to (mis)use for that purpose.
/// </summary>
/// <param name="ItemId">The item to plan playback for.</param>
/// <param name="UserId">The requesting user.</param>
/// <param name="Capabilities">What the requesting client can decode and wants produced when transcoding - see <see cref="PlaybackPlanRequestBase"/>.</param>
/// <param name="Constraints">The playback method/bitrate/subtitle preferences and limits for this request - see <see cref="PlaybackPlanRequestBase"/>.</param>
/// <param name="MediaSourceId">Optional. A specific media source id, if playing an alternate version.</param>
/// <param name="PlaybackAttemptId">
/// Issue #43. Optional, opaque, stable across the whole playback attempt - see
/// <see cref="PlaybackPlanRequestBase"/>. A <c>PUT</c> that re-plans a session mid-attempt (track
/// change, constraint change) carries the SAME value the <c>POST</c> did; only a genuinely new
/// attempt carries a new one.
/// </param>
public sealed record ReplacePlaybackSessionRequest(
    Guid ItemId,
    Guid UserId,
    ClientCapabilities Capabilities,
    PlaybackConstraints Constraints,
    string? MediaSourceId = null,
    string? PlaybackAttemptId = null)
    : PlaybackPlanRequestBase(ItemId, UserId, Capabilities, Constraints, MediaSourceId, PlaybackAttemptId);
