using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Net;

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

    /// <summary>
    /// Gets the recorder that observes every accepted upgrade and its principal (#153-A0-R3).
    /// </summary>
    public UpgradeRecorder Recorder { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);

            // Added, never replacing: SessionWebSocketListener must stay in the pipeline, because
            // R2's watchlist assertions are about it. WebSocketManager starts every listener before
            // awaiting any of them, so the recorder sees an acceptance even when the session
            // listener throws on the same connection.
            services.AddSingleton<IWebSocketListener>(Recorder);
        });
    }
}
