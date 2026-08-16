using System;

namespace Tesserafin.Api.Models.PlaybackCredentialDtos;

/// <summary>
/// The result of extending a capability's expiry.
/// </summary>
/// <remarks>
/// Carries no secret. Renewal extends the existing capability in place rather than rotating it, so
/// the client keeps using the value it already holds and an in-flight segment request does not fail
/// mid-playback.
/// </remarks>
public class PlaybackCapabilityRenewalDto
{
    /// <summary>
    /// Gets or sets the capability that was renewed.
    /// </summary>
    public Guid CapabilityId { get; set; }

    /// <summary>
    /// Gets or sets the original issue time, which renewal does not move.
    /// </summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// Gets or sets the new expiry.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
