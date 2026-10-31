using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// Revokes the capabilities of a play session that belong to one owner, and no others.
/// </summary>
/// <remarks>
/// A play session id is the client's to choose, so two users can hold capabilities under the same
/// one. A separate interface because adding members to a published interface breaks every
/// implementation of it (see <c>ITranscodeOwnedStop</c>).
/// </remarks>
public interface IOwnedPlaySessionRevocation
{
    /// <summary>
    /// Revokes the capabilities of a play session belonging to a user, optionally on one device.
    /// </summary>
    /// <param name="playSessionId">The play session that ended.</param>
    /// <param name="userId">The user whose capabilities end.</param>
    /// <param name="deviceId">The device whose capabilities end, or <see langword="null"/> for all of the user's.</param>
    /// <returns>How many capabilities were removed.</returns>
    int RevokePlaySession(string playSessionId, Guid userId, string? deviceId);
}

/// <summary>
/// Reaches <see cref="IOwnedPlaySessionRevocation"/> from the credential service.
/// </summary>
public static class OwnedPlaySessionRevocationExtensions
{
    /// <summary>
    /// Revokes the capabilities of a play session belonging to a user, optionally on one device.
    /// </summary>
    /// <param name="service">The credential service.</param>
    /// <param name="playSessionId">The play session that ended.</param>
    /// <param name="userId">The user whose capabilities end.</param>
    /// <param name="deviceId">The device whose capabilities end, or <see langword="null"/> for all of the user's.</param>
    /// <returns>How many capabilities were removed.</returns>
    /// <exception cref="NotSupportedException">The service cannot scope a revocation: falling back to the whole play session would end other users' capabilities.</exception>
    public static int RevokePlaySession(this IPlaybackCredentialService service, string playSessionId, Guid userId, string? deviceId)
        => service is IOwnedPlaySessionRevocation owned
            ? owned.RevokePlaySession(playSessionId, userId, deviceId)
            : throw new NotSupportedException("This credential service cannot revoke a play session by its owner.");
}
