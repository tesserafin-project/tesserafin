using System;
using System.Collections.Generic;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// The binding a playback capability is issued against.
/// </summary>
/// <param name="UserId">The authenticated user.</param>
/// <param name="SessionId">The authenticated session.</param>
/// <param name="DeviceId">The device the session belongs to.</param>
/// <param name="PlaySessionId">The play session, which is what a play-session end revokes by.</param>
/// <param name="ItemId">The item, or null for a scope that is not item-bound.</param>
/// <param name="MediaSourceId">The media source, or null for a scope that is not item-bound.</param>
/// <param name="Scopes">The kinds of media this capability may fetch. Never empty.</param>
public readonly record struct PlaybackCapabilityRequest(
    Guid UserId,
    string SessionId,
    string DeviceId,
    string PlaySessionId,
    Guid? ItemId,
    string? MediaSourceId,
    IReadOnlyList<PlaybackCapabilityScope> Scopes);
