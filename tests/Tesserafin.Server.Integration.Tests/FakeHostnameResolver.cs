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

/// <summary>A resolver that never touches DNS, counts its calls, and can be held open.</summary>
public sealed class FakeHostnameResolver : IHostnameResolver
{
    private readonly ConcurrentBag<string> _requested = new();

    /// <summary>Gets every hostname the engine actually asked about.</summary>
    public IReadOnlyCollection<string> Requested => _requested;

    /// <summary>Gets how many times the resolver was entered.</summary>
    public int CallCount => _requested.Count;

    /// <summary>Gets a signal set when the resolver is entered.</summary>
    public SemaphoreSlim Entered { get; } = new(0);

    /// <summary>Gets or sets a gate the resolver waits on before answering. Null answers at once.</summary>
    public SemaphoreSlim? HoldUntilReleased { get; set; }

    /// <summary>Gets a value indicating whether the resolver observed cancellation.</summary>
    public bool ObservedCancellation { get; private set; }

    public IReadOnlyList<IPAddress> Answer { get; set; } = new[] { IPAddress.Parse("203.0.113.10") };

    public async Task<DnsObservation> ResolveAsync(string normalizedHostname, CancellationToken cancellationToken)
    {
        _requested.Add(normalizedHostname);
        Entered.Release();

        if (HoldUntilReleased is not null)
        {
            try
            {
                await HoldUntilReleased.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Recorded rather than swallowed: "the caller went away and the resolver found out"
                // is the property the cancellation test exists to prove.
                ObservedCancellation = true;
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new DnsObservation(normalizedHostname, DnsLookupOutcome.Answered, Answer);
    }
}
