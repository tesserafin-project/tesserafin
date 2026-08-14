using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers;

/// <summary>
/// Cancellation and process-wide serialization, proven over HTTP (R1-P, #248).
/// </summary>
/// <remarks>
/// These two properties cannot be established from a service descriptor or a unit test. That the
/// collector is registered as a singleton is a statement about DI; that a second HTTP request
/// genuinely waits for the first is a statement about what happens at runtime, and only the second
/// one is the invariant worth having. Likewise, that the controller passes a
/// <c>CancellationToken</c> is visible in the source; that a caller hanging up actually reaches the
/// resolver mid-lookup is not.
///
/// Each test gets its OWN factory rather than sharing a class fixture, because both of them park a
/// request inside the collector and a shared host would leak that state into every other test.
/// Every wait is bounded and signal-driven — no test asserts on elapsed time.
/// </remarks>
public sealed class RemoteAccessDiagnosticsRuntimeTests
{
    private const string Route = "/System/RemoteAccess/Diagnostics";
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(30);

    private static StringContent Body(string? hostname)
    {
        var hostnameJson = hostname is null ? "null" : $"\"{hostname}\"";
        return new StringContent(
            $$"""{"Hostname": {{hostnameJson}}, "IPv4Policy": "Unspecified", "IPv6Policy": "Unspecified"}""",
            Encoding.UTF8,
            "application/json");
    }

    private static async Task<HttpClient> ElevatedAsync(RemoteAccessDiagnosticsApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await factory.AdminTokenAsync(client));
        return client;
    }

    [Fact]
    public async Task CancellingTheHttpRequestCancelsTheHostnameLookup()
    {
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            var client = await ElevatedAsync(factory);

            // The resolver will enter and then park until released — or until the caller's token
            // fires, which is the property under test.
            var gate = new SemaphoreSlim(0);
            factory.Resolver.HoldUntilReleased = gate;

            using var caller = new CancellationTokenSource();
            var inFlight = client.PostAsync(Route, Body("cancel-me.example"), caller.Token);

            // 1-2. The request really did reach the resolver. Without this the rest would pass
            //      vacuously against a request that never got that far.
            Assert.True(
                await factory.Resolver.Entered.WaitAsync(_budget, TestContext.Current.CancellationToken),
                "the request never reached the resolver");

            // 3-4. Still pending, then the caller hangs up.
            Assert.False(inFlight.IsCompleted);
            await caller.CancelAsync();

            // 5. The client sees the cancellation.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);

            // ...and so did the resolver: the token was propagated all the way through
            // CollectAsync into the lookup, rather than being accepted and dropped.
            var deadline = DateTime.UtcNow + _budget;
            while (!factory.Resolver.ObservedCancellation && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            Assert.True(factory.Resolver.ObservedCancellation, "the resolver never observed cancellation");

            // 7-8. Release the gate and prove the endpoint still works: a cancelled request must
            //      not have left the collector's semaphore held, which would deadlock every
            //      subsequent caller.
            factory.Resolver.HoldUntilReleased = null;
            gate.Release(10);

            using var after = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task TwoConcurrentRequestsAreSerializedByOneProcessWideCollector()
    {
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            var client = await ElevatedAsync(factory);

            // The address source parks inside collection, which is inside the collector's
            // semaphore. If the collector were scoped or transient each request would hold its own
            // semaphore, both would enter, and MaxObservedConcurrency would reach 2.
            var gate = new SemaphoreSlim(0);
            factory.Addresses.HoldUntilReleased = gate;

            var first = client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);
            Assert.True(
                await factory.Addresses.Entered.WaitAsync(_budget, TestContext.Current.CancellationToken),
                "the first request never entered collection");

            var second = client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);

            // 3. The second must NOT have entered collection. Given a bounded grace period to
            //    reach the collector, it should still be waiting on the shared semaphore.
            Assert.False(
                await factory.Addresses.Entered.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
                "a second request entered collection while the first still held it");
            Assert.False(second.IsCompleted);

            // 4-5. Release and let both finish.
            gate.Release(10);
            factory.Addresses.HoldUntilReleased = null;

            using var firstResponse = await first;
            using var secondResponse = await second;
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

            // 6. Never more than one collection in flight.
            Assert.Equal(1, factory.Addresses.MaxObservedConcurrency);

            // 7-8. Each caller got its own report, so nothing was reused or cached.
            using var a = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using var b = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.NotEqual(
                a.RootElement.GetProperty("CollectedAt").GetString(),
                b.RootElement.GetProperty("CollectedAt").GetString());
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }
}
