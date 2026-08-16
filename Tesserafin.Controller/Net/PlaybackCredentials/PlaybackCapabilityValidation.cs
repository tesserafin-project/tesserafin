using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// The outcome of validating a presented capability against a route's demand.
/// </summary>
/// <param name="IsValid">Whether the request may proceed on this capability.</param>
/// <param name="Failure">Why not, when it may not.</param>
/// <param name="UserId">The bound user, on success.</param>
/// <param name="SessionId">The bound session, on success.</param>
/// <param name="PlaySessionId">The bound play session, on success.</param>
public readonly record struct PlaybackCapabilityValidation(
    bool IsValid,
    PlaybackCapabilityFailure Failure,
    Guid UserId,
    string? SessionId,
    string? PlaySessionId);
