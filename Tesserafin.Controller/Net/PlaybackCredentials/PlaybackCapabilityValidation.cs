using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// The outcome of resolving or validating a presented capability.
/// </summary>
/// <param name="IsValid">Whether the request may proceed on this capability.</param>
/// <param name="Failure">Why not, when it may not.</param>
/// <param name="CapabilityId">The public identifier of the resolved capability, on success.</param>
/// <param name="UserId">The bound user, on success.</param>
/// <param name="SessionId">The bound session, on success.</param>
/// <param name="PlaySessionId">The bound play session, on success.</param>
public readonly record struct PlaybackCapabilityValidation(
    bool IsValid,
    PlaybackCapabilityFailure Failure,
    Guid CapabilityId,
    Guid UserId,
    string? SessionId,
    string? PlaySessionId);
