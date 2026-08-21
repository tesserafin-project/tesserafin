using System;
using System.Linq;
using Tesserafin.Controller.Session;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Answers "which device is this credential" the same way on both sides of the HLS ownership
/// comparison (#153-LTV-R3).
/// </summary>
/// <remarks>
/// WHY IT IS SHARED. A transcoding job records the device it was started from, and every request
/// for that job's output is compared against it. If the recording side and the comparing side
/// derived the device differently, a job started with one kind of credential would be unreachable
/// by the very same client on its next request. That is not hypothetical: a durable token carries
/// a <c>Tesserafin-DeviceId</c> claim and a playback capability does not — the capability
/// authentication handler mints a user, a capability id, a play session and nothing else — so a
/// segment request, which is the request that carries a capability, would stamp a device-less job.
///
/// The rule, once, for both sides: a durable token's device claim is the device. A capability has
/// none, so its device is the one belonging to the session it was minted for. A credential that
/// resolves to neither has no device, and an absent device never matches anything.
/// </remarks>
public static class HlsJobOwnerDevice
{
    /// <summary>
    /// Resolves the device of a credential.
    /// </summary>
    /// <param name="durableDeviceClaim">The <c>Tesserafin-DeviceId</c> claim, when the credential has one.</param>
    /// <param name="capabilitySessionId">The session a validated playback capability belongs to, when the credential is one.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <returns>The device id, or <see langword="null"/> when the credential names none.</returns>
    public static string? Resolve(string? durableDeviceClaim, string? capabilitySessionId, ISessionManager sessionManager)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);

        if (!string.IsNullOrEmpty(durableDeviceClaim))
        {
            return durableDeviceClaim;
        }

        if (string.IsNullOrEmpty(capabilitySessionId))
        {
            return null;
        }

        // A capability whose session no longer exists resolves to no device, and therefore matches
        // no job — including the job it was minted for.
        var session = sessionManager.Sessions
            .FirstOrDefault(s => string.Equals(s.Id, capabilitySessionId, StringComparison.Ordinal));

        return string.IsNullOrEmpty(session?.DeviceId) ? null : session.DeviceId;
    }
}
