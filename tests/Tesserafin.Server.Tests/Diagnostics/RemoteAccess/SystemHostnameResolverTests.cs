using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// The resolver, and the four distinct ways a lookup can fail to answer.
/// </summary>
/// <remarks>
/// No test here asks a real resolver anything. The lookup is substituted, so a timeout is proven
/// by a lookup that times out rather than by racing a live query against a deadline — the
/// distinction between "slow resolver" and "bad zone file" is exactly what must not depend on how
/// fast this machine's DNS happens to be.
/// </remarks>
public sealed class SystemHostnameResolverTests
{
    private static readonly TimeSpan _generousTimeout = TimeSpan.FromSeconds(30);

    private static SystemHostnameResolver Resolver(
        Func<string, CancellationToken, Task<IPAddress[]>> lookup,
        TimeSpan? timeout = null)
        => new(timeout ?? _generousTimeout, lookup);

    [Fact]
    public async Task AnAnswerIsReportedAsAnswered()
    {
        var resolver = Resolver((_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") }));

        var observation = await resolver.ResolveAsync("media.example.org", CancellationToken.None);

        Assert.Equal(DnsLookupOutcome.Answered, observation.Outcome);
        Assert.Equal("media.example.org", observation.NormalizedHostname);
        Assert.Single(observation.Addresses);
    }

    [Fact]
    public async Task AnEmptyAnswerIsReportedAsNoAddressRecords()
    {
        var resolver = Resolver((_, _) => Task.FromResult(Array.Empty<IPAddress>()));

        var observation = await resolver.ResolveAsync("media.example.org", CancellationToken.None);

        Assert.Equal(DnsLookupOutcome.NoAddressRecords, observation.Outcome);
        Assert.Empty(observation.Addresses);
    }

    [Fact]
    public async Task HostNotFoundIsReportedAsNoAddressRecordsRatherThanFailure()
    {
        // NXDOMAIN is an answer: the name does not exist. Reporting it as a resolver failure would
        // send an operator to debug their resolver over a record they simply have not created.
        var resolver = Resolver((_, _) => throw new SocketException((int)SocketError.HostNotFound));

        var observation = await resolver.ResolveAsync("media.example.org", CancellationToken.None);

        Assert.Equal(DnsLookupOutcome.NoAddressRecords, observation.Outcome);
    }

    [Fact]
    public async Task AResolverErrorIsReportedAsResolverFailure()
    {
        var resolver = Resolver((_, _) => throw new SocketException((int)SocketError.TryAgain));

        var observation = await resolver.ResolveAsync("media.example.org", CancellationToken.None);

        Assert.Equal(DnsLookupOutcome.ResolverFailure, observation.Outcome);
        Assert.Empty(observation.Addresses);
    }

    [Fact]
    public async Task ADeadlineExpiryIsReportedAsATimeoutNotACancellation()
    {
        var resolver = Resolver(
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return Array.Empty<IPAddress>();
            },
            TimeSpan.FromMilliseconds(20));

        var observation = await resolver.ResolveAsync("media.example.org", CancellationToken.None);

        Assert.Equal(DnsLookupOutcome.TimedOut, observation.Outcome);
    }

    [Fact]
    public async Task ACallerGivingUpIsReportedAsCancellationNotATimeout()
    {
        // Cancellation is the caller changing their mind. The linked token cannot tell the two
        // apart on its own, which is why the outer token is consulted directly.
        using var cancellation = new CancellationTokenSource();
        var resolver = Resolver(async (_, token) =>
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return Array.Empty<IPAddress>();
        });

        var observation = await resolver.ResolveAsync("media.example.org", cancellation.Token);

        Assert.Equal(DnsLookupOutcome.Cancelled, observation.Outcome);
    }

    [Fact]
    public async Task AnswersAreDeduplicatedAndStablyOrdered()
    {
        var resolver = Resolver((_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("203.0.113.9"),
            IPAddress.Parse("203.0.113.7"),
            IPAddress.Parse("203.0.113.7"),
            IPAddress.Parse("2001:db8::1")
        }));

        var observation = await resolver.ResolveAsync("media.example.org", CancellationToken.None);

        Assert.Equal(
            new[] { "203.0.113.7", "203.0.113.9", "2001:db8::1" },
            observation.Addresses.Select(a => a.ToString()));
    }

    [Fact]
    public async Task AMappedAndUnmappedFormOfOneAnswerCollapse()
    {
        var resolver = Resolver((_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("203.0.113.7"),
            IPAddress.Parse("::ffff:203.0.113.7")
        }));

        var observation = await resolver.ResolveAsync("media.example.org", CancellationToken.None);

        Assert.Single(observation.Addresses);
    }

    [Fact]
    public void TheResolverCarriesNoStateBetweenLookups()
    {
        // A cached answer would let a diagnostic describe a network that no longer exists.
        var fields = typeof(SystemHostnameResolver).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.All(fields, f => Assert.True(f.IsInitOnly, $"{f.Name} is mutable state on a resolver that must not cache."));
    }
}
