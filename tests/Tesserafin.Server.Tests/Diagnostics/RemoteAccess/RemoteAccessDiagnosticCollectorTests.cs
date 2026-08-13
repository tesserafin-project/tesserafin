using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.Diagnostics.RemoteAccess;

/// <summary>
/// How the collectors are driven, and what the collector refuses to do while driving them.
/// </summary>
public sealed class RemoteAccessDiagnosticCollectorTests
{
    private static readonly DateTimeOffset _fixedInstant = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static RemoteAccessDiagnosticCollector Build(
        FakeAddresses? addresses = null,
        FakeListeners? listeners = null,
        FakeResolver? resolver = null,
        FakePosture? posture = null,
        FixedTimeProvider? time = null)
        => new(
            addresses ?? new FakeAddresses(),
            listeners ?? new FakeListeners(),
            resolver ?? new FakeResolver(),
            posture ?? new FakePosture(),
            time ?? new FixedTimeProvider(_fixedInstant));

    [Fact]
    public async Task OnlyPorts80And443AreEverExamined()
    {
        var listeners = new FakeListeners();
        using var collector = Build(listeners: listeners);

        await collector.CollectAsync(new PublicationPolicyInput(null, true, false), CancellationToken.None);

        Assert.Equal(new[] { 80, 443 }, listeners.RequestedPorts);
    }

    [Fact]
    public async Task AnInvalidHostnameNeverReachesTheResolver()
    {
        // The input boundary has to hold before the only outbound operation in the slice, not
        // after it.
        var resolver = new FakeResolver();
        using var collector = Build(resolver: resolver);

        var report = await collector.CollectAsync(
            new PublicationPolicyInput("https://media.example.org/admin", true, false),
            CancellationToken.None);

        Assert.Equal(0, resolver.Calls);
        Assert.Equal(DnsLookupOutcome.NotAttempted, report.Snapshot.Dns.Outcome);
        Assert.True(report.Has(RemoteAccessDiagnosticCode.HostnameSyntacticallyInvalid));
    }

    [Fact]
    public async Task AnAbsentHostnameNeverReachesTheResolver()
    {
        var resolver = new FakeResolver();
        using var collector = Build(resolver: resolver);

        var report = await collector.CollectAsync(new PublicationPolicyInput(null, true, false), CancellationToken.None);

        Assert.Equal(0, resolver.Calls);
        Assert.True(report.Has(RemoteAccessDiagnosticCode.HostnameNotProvided));
    }

    [Fact]
    public async Task AValidHostnameReachesTheResolverInItsNormalizedForm()
    {
        var resolver = new FakeResolver();
        using var collector = Build(resolver: resolver);

        await collector.CollectAsync(new PublicationPolicyInput("MEDIA.Example.ORG", true, false), CancellationToken.None);

        Assert.Equal(1, resolver.Calls);
        Assert.Equal("media.example.org", resolver.LastHostname);
    }

    [Fact]
    public async Task TheCollectionInstantComesFromTheInjectedClock()
    {
        var time = new FixedTimeProvider(_fixedInstant);
        using var collector = Build(time: time);

        var report = await collector.CollectAsync(new PublicationPolicyInput(null, true, false), CancellationToken.None);

        Assert.Equal(_fixedInstant, report.Snapshot.CollectedAt);
    }

    [Fact]
    public async Task CancellationPropagatesIntoTheResolver()
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new FakeResolver
        {
            Before = async _ => await cancellation.CancelAsync().ConfigureAwait(false)
        };

        using var collector = Build(resolver: resolver);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(
            new PublicationPolicyInput("media.example.org", true, false),
            cancellation.Token));
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenStopsCollectionBeforeItStarts()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var posture = new FakePosture();
        using var collector = Build(posture: posture);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(
            new PublicationPolicyInput("media.example.org", true, false),
            cancellation.Token));

        Assert.Equal(0, posture.BackendReads);
    }

    [Fact]
    public async Task CollectionsDoNotOverlap()
    {
        // An authenticated administrator must not be able to turn a diagnostic into an amplifier
        // by starting an unbounded number of listener-table reads at once.
        var released = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var concurrent = 0;
        var peak = 0;

        var resolver = new FakeResolver
        {
            Before = async _ =>
            {
                var now = Interlocked.Increment(ref concurrent);
                peak = Math.Max(peak, now);
                entered.TrySetResult();
                await released.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref concurrent);
            }
        };

        using var collector = Build(resolver: resolver);
        var input = new PublicationPolicyInput("media.example.org", true, false);

        var first = collector.CollectAsync(input, CancellationToken.None);
        await entered.Task;
        var second = collector.CollectAsync(input, CancellationToken.None);

        Assert.False(second.IsCompleted);

        released.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task NothingIsCachedBetweenCollections()
    {
        // A diagnostic that answers with a previous network's state is worse than no diagnostic.
        var posture = new FakePosture();
        using var collector = Build(posture: posture);
        var input = new PublicationPolicyInput(null, true, false);

        await collector.CollectAsync(input, CancellationToken.None);
        await collector.CollectAsync(input, CancellationToken.None);
        await collector.CollectAsync(input, CancellationToken.None);

        Assert.Equal(3, posture.BackendReads);
    }

    [Fact]
    public async Task LocalAddressesAreNormalizedAndDeduplicated()
    {
        var addresses = new FakeAddresses
        {
            Addresses = new[]
            {
                IPAddress.Parse("192.168.1.5"),
                IPAddress.Parse("::ffff:192.168.1.5"),
                IPAddress.Parse("192.168.1.5")
            }
        };

        using var collector = Build(addresses: addresses);
        var report = await collector.CollectAsync(new PublicationPolicyInput(null, true, false), CancellationToken.None);

        Assert.Single(report.Snapshot.LocalAddresses);
    }

    [Fact]
    public async Task TheReportCarriesTheCurrentSchemaVersion()
    {
        using var collector = Build();
        var report = await collector.CollectAsync(new PublicationPolicyInput(null, true, false), CancellationToken.None);

        Assert.Equal(RemoteAccessDiagnosticReport.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal(1, report.SchemaVersion);
    }

    [Fact]
    public async Task NullInputThrows()
    {
        using var collector = Build();
        await Assert.ThrowsAsync<ArgumentNullException>(() => collector.CollectAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// A clock that never moves, so a report's instant is asserted rather than tolerated.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeAddresses : ILocalAddressSource
    {
        public IReadOnlyList<IPAddress> Addresses { get; init; } = Array.Empty<IPAddress>();

        public IReadOnlyList<IPAddress> GetUnicastAddresses() => Addresses;
    }

    private sealed class FakeListeners : ITcpListenerSource
    {
        public List<int> RequestedPorts { get; } = new();

        public ListenerObservationOutcome Outcome { get; init; } = ListenerObservationOutcome.NoListenerObserved;

        public IReadOnlyList<PortListenerObservation> Observe(IReadOnlyList<int> ports)
        {
            RequestedPorts.AddRange(ports);
            return ports.Select(p => new PortListenerObservation(p, Outcome)).ToList();
        }
    }

    private sealed class FakeResolver : IHostnameResolver
    {
        public int Calls { get; private set; }

        public string? LastHostname { get; private set; }

        public DnsObservation Result { get; init; } =
            new(null, DnsLookupOutcome.NoAddressRecords, Array.Empty<IPAddress>());

        public Func<CancellationToken, Task>? Before { get; init; }

        public async Task<DnsObservation> ResolveAsync(string normalizedHostname, CancellationToken cancellationToken)
        {
            Calls++;
            LastHostname = normalizedHostname;

            if (Before is not null)
            {
                await Before(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Result with { NormalizedHostname = normalizedHostname };
        }
    }

    private sealed class FakePosture : INetworkPostureSource
    {
        public int BackendReads { get; private set; }

        public BackendPostureObservation Backend { get; init; } =
            new(true, BackendBindPosture.LoopbackOnly, false, 8096, 8920);

        public ProxyTrustObservation Trust { get; init; } =
            new(Array.Empty<string>(), 0, false);

        public BackendPostureObservation GetBackendPosture()
        {
            BackendReads++;
            return Backend;
        }

        public ProxyTrustObservation GetProxyTrust() => Trust;
    }
}
