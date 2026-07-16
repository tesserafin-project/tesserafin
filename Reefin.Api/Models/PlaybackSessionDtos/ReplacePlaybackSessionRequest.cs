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
public sealed record ReplacePlaybackSessionRequest(
    Guid ItemId,
    Guid UserId,
    ClientCapabilities Capabilities,
    PlaybackConstraints Constraints,
    string? MediaSourceId = null)
    : PlaybackPlanRequestBase(ItemId, UserId, Capabilities, Constraints, MediaSourceId);
