using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Resolves a hostname through the system resolver, and stops there.
/// </summary>
/// <remarks>
/// <para>
/// The only outbound thing this whole slice does. It asks the resolver the host is already
/// configured to use — no custom resolver, no DNS-over-anything, no DNS-provider API, no WHOIS,
/// no HTTP, and no write. CNAME following is whatever the system resolver ordinarily does; this
/// class does not chase records itself.
/// </para>
/// <para>
/// It never connects to an address it receives. That is the difference between a name lookup and
/// a server-side request forgery primitive, and it is enforced structurally by a test that reads
/// this source file, because an absence cannot be observed by calling something.
/// </para>
/// </remarks>
public sealed class SystemHostnameResolver : IHostnameResolver
{
    private readonly TimeSpan _timeout;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _lookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemHostnameResolver"/> class.
    /// </summary>
    /// <param name="timeout">The bounded deadline for a single lookup.</param>
    public SystemHostnameResolver(TimeSpan timeout)
        : this(timeout, static (host, token) => Dns.GetHostAddressesAsync(host, token))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemHostnameResolver"/> class with a
    /// substitute lookup.
    /// </summary>
    /// <remarks>
    /// Exists so the four ways a lookup can fail to answer can be proven deterministically without
    /// asking a real resolver anything. Racing a live query against a deadline would make the test
    /// suite depend on how fast the machine's DNS happens to be that morning, and the distinction
    /// between a timeout and a resolver failure is precisely what must not be left to chance.
    /// </remarks>
    /// <param name="timeout">The bounded deadline for a single lookup.</param>
    /// <param name="lookup">The lookup to perform.</param>
    internal SystemHostnameResolver(TimeSpan timeout, Func<string, CancellationToken, Task<IPAddress[]>> lookup)
    {
        _timeout = timeout;
        _lookup = lookup;
    }

    /// <inheritdoc />
    public async Task<DnsObservation> ResolveAsync(string normalizedHostname, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            var answers = await _lookup(normalizedHostname, deadline.Token).ConfigureAwait(false);
            var ordered = AddressClassifier.ClassifySet(answers);

            var addresses = new List<IPAddress>(ordered.Count);
            foreach (var entry in ordered)
            {
                addresses.Add(entry.Address);
            }

            return new DnsObservation(
                normalizedHostname,
                addresses.Count == 0 ? DnsLookupOutcome.NoAddressRecords : DnsLookupOutcome.Answered,
                addresses);
        }
        catch (OperationCanceledException)
        {
            // The caller giving up and the deadline expiring are different events with different
            // remedies, and the linked token cannot tell them apart on its own.
            var outcome = cancellationToken.IsCancellationRequested
                ? DnsLookupOutcome.Cancelled
                : DnsLookupOutcome.TimedOut;
            return new DnsObservation(normalizedHostname, outcome, Array.Empty<IPAddress>());
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
        {
            return new DnsObservation(normalizedHostname, DnsLookupOutcome.NoAddressRecords, Array.Empty<IPAddress>());
        }
        catch (SocketException)
        {
            return new DnsObservation(normalizedHostname, DnsLookupOutcome.ResolverFailure, Array.Empty<IPAddress>());
        }
        catch (ArgumentException)
        {
            return new DnsObservation(normalizedHostname, DnsLookupOutcome.ResolverFailure, Array.Empty<IPAddress>());
        }
    }
}
