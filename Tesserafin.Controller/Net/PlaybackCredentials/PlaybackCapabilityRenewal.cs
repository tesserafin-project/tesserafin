using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// The outcome of a renewal. Carries no secret: the client already holds the value, and renewal
/// extends the existing capability rather than rotating it.
/// </summary>
/// <param name="Succeeded">Whether the expiry moved.</param>
/// <param name="Failure">Why not, when it did not.</param>
/// <param name="CapabilityId">The capability that was renewed.</param>
/// <param name="IssuedAt">Unchanged by renewal — the original issue time.</param>
/// <param name="ExpiresAt">The new expiry on success.</param>
public readonly record struct PlaybackCapabilityRenewal(
    bool Succeeded,
    PlaybackCapabilityFailure Failure,
    Guid CapabilityId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
