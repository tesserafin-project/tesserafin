using System;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Deterministic canary cohort membership (PR115a): a user/device pair hashes to a stable bucket in
/// [0, 100), and is in the cohort when its bucket falls below the configured percentage. The same
/// pair therefore always gets the same answer for a given percentage - across requests, sessions,
/// and server restarts - and raising the percentage only ever ADDS pairs to the cohort (a pair in
/// at 5% is still in at 20%), which is what makes a gradual rollout coherent. This is deliberately
/// not <see cref="Random"/>: a per-request draw would flip the serving engine mid-session and make
/// any canary incident impossible to reproduce or scope.
/// </summary>
public static class CanaryCohort
{
    // FNV-1a 32-bit. Chosen over GetHashCode() (randomized per process since .NET Core, so not
    // stable across restarts) and over cryptographic hashes (needless cost per planning call).
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>
    /// Decides whether a user/device pair is in the canary cohort at the given percentage.
    /// </summary>
    /// <param name="userId">The requesting user. <see cref="Guid.Empty"/> is hashed like any other value, so anonymous flows still bucket deterministically per device.</param>
    /// <param name="deviceId">The requesting device, or <see langword="null"/>/empty when unknown - the bucket then depends on the user alone.</param>
    /// <param name="percentage">The cohort size in [0, 100]. 0 enrolls nobody; 100 enrolls everybody.</param>
    /// <returns><see langword="true"/> when the pair's stable bucket falls below <paramref name="percentage"/>.</returns>
    public static bool IsInCohort(Guid userId, string? deviceId, int percentage)
    {
        if (percentage <= 0)
        {
            return false;
        }

        if (percentage >= 100)
        {
            return true;
        }

        return Bucket(userId, deviceId) < percentage;
    }

    /// <summary>
    /// Computes the stable bucket, in [0, 100), for a user/device pair. Exposed (rather than
    /// private) so diagnostics can show WHY a session was or was not canary-served.
    /// </summary>
    /// <param name="userId">The requesting user.</param>
    /// <param name="deviceId">The requesting device, or <see langword="null"/>/empty when unknown.</param>
    /// <returns>The pair's bucket.</returns>
    public static int Bucket(Guid userId, string? deviceId)
    {
        var hash = FnvOffsetBasis;

        Span<byte> userBytes = stackalloc byte[16];
        userId.TryWriteBytes(userBytes);
        foreach (var b in userBytes)
        {
            hash = (hash ^ b) * FnvPrime;
        }

        if (!string.IsNullOrEmpty(deviceId))
        {
            // Case-insensitive: device ids are client-supplied strings and must not fall in or out
            // of the cohort on a casing difference between two requests from the same device.
            foreach (var c in deviceId)
            {
                var lower = char.ToLowerInvariant(c);
                hash = (hash ^ (byte)lower) * FnvPrime;
                hash = (hash ^ (byte)(lower >> 8)) * FnvPrime;
            }
        }

        return (int)(hash % 100);
    }
}
