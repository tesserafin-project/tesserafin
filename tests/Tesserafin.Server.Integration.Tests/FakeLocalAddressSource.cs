using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tesserafin.Server.Diagnostics.RemoteAccess;

namespace Tesserafin.Server.Integration.Tests;

/// <summary>Fixed local addresses, and a gate so collection can be held mid-flight.</summary>
public sealed class FakeLocalAddressSource : ILocalAddressSource
{
    private int _concurrent;

    /// <summary>Gets the highest number of collections observed inside the source at once.</summary>
    public int MaxObservedConcurrency { get; private set; }

    /// <summary>Gets a signal set on entry.</summary>
    public SemaphoreSlim Entered { get; } = new(0);

    /// <summary>Gets or sets a gate to hold collection open. Null returns at once.</summary>
    public SemaphoreSlim? HoldUntilReleased { get; set; }

    public IReadOnlyList<IPAddress> GetUnicastAddresses()
    {
        var now = Interlocked.Increment(ref _concurrent);
        MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, now);
        Entered.Release();

        // Blocking on purpose: the collector's invariant is a semaphore held across the whole
        // collection, so holding it here is what makes a second HTTP request wait — or, if the
        // collector were ever registered per request, not wait, which is the failure being gated.
        HoldUntilReleased?.Wait();

        Interlocked.Decrement(ref _concurrent);
        return new[]
        {
            IPAddress.Loopback,
            IPAddress.Parse("192.168.1.20"),
            IPAddress.Parse("203.0.113.10")
        };
    }
}
