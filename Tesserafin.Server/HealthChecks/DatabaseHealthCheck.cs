using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Tesserafin.Server.HealthChecks;

/// <summary>
/// The <c>database</c> entry of the <c>/health</c> report (#91 / [A5]).
/// </summary>
/// <remarks>
/// Deliberately thin: it owns the health-check contract and the upper time bound, and delegates the
/// actual database work to <see cref="IDatabaseHealthProbe"/> — the single dependency an
/// integration test replaces to exercise the endpoint's 503 branch.
/// </remarks>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    /// <summary>
    /// The registration name of this check. It is also the JSON field name under which the result
    /// is published by <see cref="HealthResponseWriter"/>, so it is part of the wire contract and
    /// must not be renamed without a contract change.
    /// </summary>
    public const string Name = "database";

    /// <summary>
    /// Upper bound for one probe. Enforced here, on the consuming side, so that <em>any</em>
    /// <see cref="IDatabaseHealthProbe"/> that honours its cancellation token is bounded — the
    /// endpoint cannot be made to hang by a slow or wedged database.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IDatabaseHealthProbe _probe;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseHealthCheck"/> class.
    /// </summary>
    /// <param name="probe">The database probe.</param>
    public DatabaseHealthCheck(IDatabaseHealthProbe probe)
    {
        _probe = probe;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ProbeTimeout);

        try
        {
            var reachable = await _probe.IsReachableAsync(bounded.Token).ConfigureAwait(false);

            // No Description and no Exception are attached on purpose: HealthResponseWriter never
            // serialises them, and anything carried here would only invite a future change to do so.
            return reachable ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The probe ran out of time rather than the caller giving up: that is an unhealthy
            // database, not a cancelled request.
            return HealthCheckResult.Unhealthy();
        }
    }
}
