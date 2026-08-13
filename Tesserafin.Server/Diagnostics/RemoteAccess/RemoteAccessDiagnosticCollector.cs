using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Runs the collectors once, freezes what they said, and hands it to the evaluator.
/// </summary>
/// <remarks>
/// <para>
/// The only stateful piece of the layer, and it holds exactly one thing: a semaphore that keeps
/// two collections from running at once. Collection reads the whole listener table and asks a
/// resolver a question; letting an authenticated administrator start an unbounded number of those
/// in parallel would turn a diagnostic into an amplifier.
/// </para>
/// <para>
/// Time is taken once, here, and carried in the snapshot. No classification rule reads a clock,
/// which is what makes evaluation deterministic. Nothing is cached between calls — a diagnostic
/// that answers with a previous network's state is worse than no diagnostic.
/// </para>
/// </remarks>
public sealed class RemoteAccessDiagnosticCollector : IDisposable
{
    /// <summary>The ports an ingress would occupy, and the only ports ever examined.</summary>
    private static readonly int[] _ingressPorts = { 80, 443 };

    private readonly ILocalAddressSource _localAddresses;
    private readonly ITcpListenerSource _listeners;
    private readonly IHostnameResolver _resolver;
    private readonly INetworkPostureSource _posture;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteAccessDiagnosticCollector"/> class.
    /// </summary>
    /// <param name="localAddresses">Supplies local unicast addresses.</param>
    /// <param name="listeners">Observes ingress ports.</param>
    /// <param name="resolver">Resolves the proposed hostname.</param>
    /// <param name="posture">Reads the server's own networking configuration.</param>
    /// <param name="timeProvider">Stamps the collection instant.</param>
    public RemoteAccessDiagnosticCollector(
        ILocalAddressSource localAddresses,
        ITcpListenerSource listeners,
        IHostnameResolver resolver,
        INetworkPostureSource posture,
        TimeProvider timeProvider)
    {
        _localAddresses = localAddresses;
        _listeners = listeners;
        _resolver = resolver;
        _posture = posture;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Collects a snapshot and evaluates it.
    /// </summary>
    /// <param name="input">What the caller proposes to publish.</param>
    /// <param name="cancellationToken">Cancels collection, including the lookup.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
    public async Task<RemoteAccessDiagnosticReport> CollectAsync(PublicationPolicyInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var backend = _posture.GetBackendPosture();
            var proxyTrust = _posture.GetProxyTrust();
            var addresses = AddressClassifier.ClassifySet(_localAddresses.GetUnicastAddresses());
            var listeners = _listeners.Observe(_ingressPorts);

            // Validation happens before anything is resolved, so a rejected value never reaches
            // the resolver at all. NotAttempted is a distinct outcome precisely so the report can
            // say "this was never asked" rather than implying a lookup that failed.
            var dns = HostnameInput.TryNormalize(input.ProposedHostname, out var normalized) && normalized is not null
                ? await _resolver.ResolveAsync(normalized, cancellationToken).ConfigureAwait(false)
                : new DnsObservation(null, DnsLookupOutcome.NotAttempted, Array.Empty<System.Net.IPAddress>());

            var snapshot = new RemoteAccessDiagnosticSnapshot(
                _timeProvider.GetUtcNow(),
                input,
                backend,
                proxyTrust,
                addresses,
                listeners,
                dns);

            return RemoteAccessDiagnosticEvaluator.Evaluate(snapshot);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _oneAtATime.Dispose();
    }

    /// <summary>
    /// Gets the ports this collector examines.
    /// </summary>
    /// <returns>The ingress ports.</returns>
    internal static IReadOnlyList<int> IngressPorts() => _ingressPorts;
}
