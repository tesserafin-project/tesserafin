using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// The media-boundary server plus a log sink, so #153-A0-R2 can assert that no ticket value ever
/// reaches a log line, a structured state value or a scope.
/// </summary>
/// <remarks>
/// The credential-service clock substitution is inherited, which is what lets the expiry case move
/// time by hand instead of sleeping for thirty seconds.
/// </remarks>
public sealed class WebSocketUpgradeApplicationFactory : MediaBoundaryApplicationFactory
{
    /// <summary>Gets everything the application has logged.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
            services.AddSingleton<ILoggerProvider>(Logs));
    }
}
