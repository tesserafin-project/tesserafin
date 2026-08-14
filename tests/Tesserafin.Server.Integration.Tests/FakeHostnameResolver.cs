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

    /// <summary>Gets a signal set when the resolver observes cancellation.</summary>
    /// <remarks>
    /// A SIGNAL, NOT A POLL. The cancellation test used to spin on <see cref="ObservedCancellation"/>
    /// until a deadline; with a token that never cancels that spin simply burned its budget and left
    /// the resolver parked, which is how a broken propagation turned into a hung testhost instead of
    /// a failed assertion.
    /// </remarks>
    public SemaphoreSlim CancellationObserved { get; } = new(0);

    /// <summary>Gets a signal set when the resolver leaves, however it leaves.</summary>
    public SemaphoreSlim Exited { get; } = new(0);

    /// <summary>Gets a value indicating whether the last token handed to the resolver could ever cancel.</summary>
    /// <remarks>
    /// <c>CancellationToken.None.CanBeCanceled</c> is false, so this alone separates "the controller
    /// propagated the request's token" from "the controller propagated nothing" - without waiting
    /// for a cancellation that, in the broken case, can never arrive.
    /// </remarks>
    public bool LastTokenCanBeCanceled { get; private set; }

    public IReadOnlyList<IPAddress> Answer { get; set; } = new[] { IPAddress.Parse("203.0.113.10") };

    public async Task<DnsObservation> ResolveAsync(string normalizedHostname, CancellationToken cancellationToken)
    {
        _requested.Add(normalizedHostname);
        LastTokenCanBeCanceled = cancellationToken.CanBeCanceled;
        Entered.Release();

        try
        {
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
                    CancellationObserved.Release();
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new DnsObservation(normalizedHostname, DnsLookupOutcome.Answered, Answer);
        }
        finally
        {
            // However this call leaves - answered, cancelled or released by the test - the test can
            // wait for the exit with a bound instead of guessing.
            Exited.Release();
        }
    }
}
