using System;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// The read-only listener source, driven against whatever this machine happens to be running.
/// </summary>
/// <remarks>
/// Deliberately makes no assertion about what is or is not listening: the point is that the call
/// is safe and total, not that this host has a particular network state.
/// </remarks>
public sealed class SystemTcpListenerSourceTests
{
    [Fact]
    public void ObservingReturnsOneOutcomePerRequestedPortAndNothingElse()
    {
        var source = new SystemTcpListenerSource();

        var observations = source.Observe(new[] { 80, 443 });

        Assert.Equal(2, observations.Count);
        Assert.Equal(80, observations[0].Port);
        Assert.Equal(443, observations[1].Port);
        Assert.All(observations, o => Assert.NotEqual(ListenerObservationOutcome.None, o.Outcome));
    }

    [Fact]
    public void ObservingIsRepeatableAndDoesNotThrow()
    {
        var source = new SystemTcpListenerSource();

        var first = source.Observe(new[] { 80, 443 });
        var second = source.Observe(new[] { 80, 443 });

        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public void NullPortsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new SystemTcpListenerSource().Observe(null!));
    }
}
