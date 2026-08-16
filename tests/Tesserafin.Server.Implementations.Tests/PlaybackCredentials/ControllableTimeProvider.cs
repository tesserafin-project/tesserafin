using System;

namespace Tesserafin.Server.Implementations.Tests.PlaybackCredentials;

/// <summary>
/// A clock the test moves by hand.
/// </summary>
/// <remarks>
/// Every expiry and renewal-window assertion in this file advances THIS, and nothing sleeps. A
/// sleeping test would have to sleep fifteen real minutes to observe a capability expiring, so in
/// practice such a suite either never tests expiry or tests it with a shortened production
/// constant, which then proves the property for a configuration nobody ships.
/// </remarks>
public sealed class ControllableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="delta">How far forward.</param>
    public void Advance(TimeSpan delta) => _now += delta;
}
