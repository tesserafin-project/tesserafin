using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Server.HealthChecks;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// A database probe whose behaviour a test flips at will.
    /// </summary>
    /// <remarks>
    /// In <see cref="HealthProbeMode.Real"/> this is not a stub: it constructs and runs the
    /// production <see cref="DatabaseHealthProbe"/> against the real SQLite database the test host
    /// created. Only the failing and never-answering modes substitute anything, which is what makes
    /// the 503 branch of <c>/health</c> reachable without a production failpoint (#91 / [A5]).
    /// </remarks>
    public sealed class SwitchableDatabaseHealthProbe : IDatabaseHealthProbe
    {
        private readonly IServiceProvider _services;

        public SwitchableDatabaseHealthProbe(IServiceProvider services)
        {
            _services = services;
        }

        public HealthProbeMode Mode { get; set; } = HealthProbeMode.Real;

        public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
        {
            switch (Mode)
            {
                case HealthProbeMode.Fail:
                    return false;
                case HealthProbeMode.Hang:
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return true;
                default:
                    var real = ActivatorUtilities.CreateInstance<DatabaseHealthProbe>(_services);
                    return await real.IsReachableAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
