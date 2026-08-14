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

    // Small, separate and documented. The cancellation test must finish - pass or fail - inside
    // the sum of these, which is an order of magnitude below any harness timeout.
    private static readonly TimeSpan _enterBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _observeBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _exitBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _settleBudget = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// Cancelling the HTTP request must cancel the hostname lookup inside the collector.
    /// </summary>
    /// <remarks>
    /// EVERY WAIT IS BOUNDED AND THE RESOLVER IS ALWAYS RELEASED. The first version of this test
    /// polled a flag for thirty seconds and only released the resolver on the success path, so a
    /// controller that propagated <c>CancellationToken.None</c> left the resolver parked forever:
    /// the request stayed in flight, host shutdown waited for it, and a broken propagation
    /// presented as a hung testhost rather than a failed assertion. A gate that can only fail by
    /// hanging is not a gate.
    ///
    /// The structure below records four separate facts - whether the propagated token could cancel
    /// at all, whether cancellation was observed, whether the resolver left, and whether the request
    /// settled - releases the resolver in a finally regardless, and only then asserts. Under
    /// <c>CancellationToken.None</c> the first two are false and the test fails on a named
    /// assertion within seconds.
    /// </remarks>
    /// <returns>A task that completes when the case has finished.</returns>
    [Fact]
    public async Task CancellingTheHttpRequestCancelsTheHostnameLookup()
    {
        var factory = new RemoteAccessDiagnosticsApplicationFactory();
        try
        {
            var client = await ElevatedAsync(factory);

            // The resolver parks here until the test releases it - which the test always does -
            // or until the caller's token fires, which is the property under test.
            var gate = new SemaphoreSlim(0);
            factory.Resolver.HoldUntilReleased = gate;

            using var caller = new CancellationTokenSource();
            var inFlight = client.PostAsync(Route, Body("cancel-me.example"), caller.Token);

            bool entered;
            var tokenCanBeCanceled = false;
            var observedCancellation = false;
            bool resolverExited;
            bool requestSettled;

            try
            {
                // 1. The request really did reach the resolver. Without this the rest would pass
                //    vacuously against a request that never got that far.
                entered = await factory.Resolver.Entered.WaitAsync(_enterBudget, TestContext.Current.CancellationToken);
                Assert.True(entered, "the request never reached the resolver");

                // 2. What the controller actually handed down. CancellationToken.None cannot cancel,
                //    so this separates "propagated the request token" from "propagated nothing"
                //    without waiting for a cancellation that may never arrive.
                tokenCanBeCanceled = factory.Resolver.LastTokenCanBeCanceled;

                // 3. Still pending, then the caller hangs up.
                Assert.False(inFlight.IsCompleted);
                await caller.CancelAsync();

                // 4. Bounded, signal-driven: no polling, no elapsed-time assertion.
                observedCancellation = await factory.Resolver.CancellationObserved.WaitAsync(
                    _observeBudget, TestContext.Current.CancellationToken);
            }
            finally
            {
                // ALWAYS. A resolver left parked would hold the collector's semaphore and hang
                // disposal, which is exactly how the previous version failed.
                factory.Resolver.HoldUntilReleased = null;
                gate.Release(16);

                resolverExited = await factory.Resolver.Exited.WaitAsync(_exitBudget, TestContext.Current.CancellationToken);
                requestSettled = await SettledAsync(inFlight, _settleBudget);
            }

            Assert.True(
                tokenCanBeCanceled,
                "the resolver received a token that can never be cancelled: the request's token was not propagated");
            Assert.True(
                observedCancellation,
                "the resolver never observed cancellation: the caller hung up and the lookup did not find out");
            Assert.True(resolverExited, "the resolver never left after being released");
            Assert.True(requestSettled, "the cancelled request never settled");

            // 5. The client saw the cancellation too.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);

            // 6. A cancelled request must not have left the collector's semaphore held, which would
            //    deadlock every subsequent caller.
            using var after = await client.PostAsync(Route, Body(null), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Waits, with a bound, for a request to settle in any way at all.</summary>
    private static async Task<bool> SettledAsync(Task<HttpResponseMessage> inFlight, TimeSpan budget)
    {
        var completed = await Task.WhenAny(inFlight, Task.Delay(budget)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, inFlight))
        {
            return false;
        }

        try
        {
            (await inFlight.ConfigureAwait(false)).Dispose();
        }
        catch (Exception)
        {
            // Cancelled or faulted both count as settled; which one it was is asserted separately.
        }

        return true;
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
