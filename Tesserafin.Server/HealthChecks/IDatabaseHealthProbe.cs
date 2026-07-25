using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.HealthChecks;

/// <summary>
/// A bounded, side-effect-free reachability probe against the application database.
/// </summary>
/// <remarks>
/// This is the seam that makes the <c>/health</c> failure path testable (#91 / [A5]). The
/// production registration executes a real statement against the real database; an integration
/// test can replace this single dependency to drive the endpoint's non-2xx branch without adding
/// a production failpoint, a debug-only switch or an undocumented environment variable.
/// Tesserafin's database is embedded SQLite, so there is no separate database process to stop.
/// </remarks>
public interface IDatabaseHealthProbe
{
    /// <summary>
    /// Determines whether the application database currently answers a trivial query.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the caller gives up (an aborted HTTP request)
    /// or when the caller's own upper bound elapses — see <see cref="DatabaseHealthCheck.ProbeTimeout"/>.
    /// Implementations must honour it so the endpoint can never hang indefinitely.</param>
    /// <returns><c>true</c> when the database answered; otherwise <c>false</c>. Implementations
    /// must not surface the underlying failure to the caller: the reason belongs in the log, not
    /// in an HTTP response.</returns>
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
}
