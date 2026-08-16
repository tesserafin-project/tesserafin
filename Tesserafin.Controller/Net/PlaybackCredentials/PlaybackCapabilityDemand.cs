using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// What a protected route requires of a presented capability.
/// </summary>
/// <param name="Scope">The single scope this route demands.</param>
/// <param name="ItemId">The item this request is for, or null for a route that is not item-bound.</param>
/// <param name="MediaSourceId">The media source this request is for, if the route names one.</param>
/// <remarks>
/// A null <paramref name="MediaSourceId"/> means "this route does not name a media source", not
/// "any media source will do" — the capability's own binding is still checked against the item.
/// </remarks>
public readonly record struct PlaybackCapabilityDemand(
    PlaybackCapabilityScope Scope,
    Guid? ItemId,
    string? MediaSourceId);
