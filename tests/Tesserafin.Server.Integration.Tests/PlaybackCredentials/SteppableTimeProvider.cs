using System;
using System.Threading;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// A clock the test moves by hand, so capability expiry is proven by the validator reading the
/// clock rather than by a test that sleeps. Nothing in this suite waits on wall time.
/// </summary>
public sealed class SteppableTimeProvider : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>
    /// Moves the clock forward.
    /// </summary>
    /// <param name="amount">How far forward to move.</param>
    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
}
