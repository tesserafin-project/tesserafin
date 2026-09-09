using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tesserafin.Server.ServiceHost;

/// <summary>
/// Runs the server underneath a Windows Service Control Manager.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/distribution/W0-windows-server.md</c> §4 selects "direct .NET Generic Host Windows
/// Service integration in <c>Tesserafin.Server</c>", measured against the alternatives: the
/// unmodified console executable fails <c>sc start</c> with error 1053 because it never calls
/// <c>StartServiceCtrlDispatcher</c>, a dedicated first-party service host would add a second
/// executable and a second lifetime, and an opaque third-party wrapper is rejected outright. This
/// file is that selection and nothing more.
/// </para>
/// <para>
/// The host built here is a <b>shell</b>. It owns exactly one hosted service, and that service
/// starts the real server without waiting for it. The reason is timing, and it is the difference
/// between this design and simply adding <c>AddWindowsService</c> to the server's own host builder.
/// <c>Host.StartAsync</c> calls <c>IHostLifetime.WaitForStartAsync</c> — where
/// <c>WindowsServiceLifetime</c> answers the SCM — <b>before</b> it starts any hosted service. On
/// the server's own host that call sits after the startup migrations, and W0 §2.3 measured a cold
/// first start still applying migrations at 180 s. The SCM's <c>ServicesPipeTimeout</c> is 30 s and
/// raising it is a machine-wide registry change this slice is not permitted to make, so a host
/// builder that reaches the dispatcher only after migrations would reproduce the very 1053 it is
/// meant to close. Answering the SCM from a shell that has nothing to do first is what makes the
/// handshake independent of how long the database takes.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsServiceEntryPoint
{
    /// <summary>
    /// The service name from <c>W0-windows-server.md</c> §4, which is also the name
    /// <c>ci/windows/w2/tesserafin-server-service.ps1</c> registers.
    /// </summary>
    internal const string ServiceName = "Tesserafin";

    /// <summary>
    /// The in-process shutdown budget, taken from §4's tabulated 120 s stop timeout.
    /// </summary>
    /// <remarks>
    /// This is the shell's own <see cref="HostOptions.ShutdownTimeout"/> and nothing else. §4's
    /// stop timeout is also a machine-wide <c>ServicesPipeTimeout</c> value, which an installer
    /// sets and this slice does not. Adopting the number here only ensures the shell is never the
    /// tighter of the two bounds; the server's own host keeps its default, so console shutdown
    /// timing is unchanged.
    /// </remarks>
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Runs <paramref name="runServer"/> under the Service Control Manager.
    /// </summary>
    /// <param name="options">The parsed startup options.</param>
    /// <param name="runServer">The server entry point, which returns its exit code.</param>
    /// <returns>The exit code the process should report.</returns>
    internal static async Task<int> RunAsync(StartupOptions options, Func<StartupOptions, Task<int>> runServer)
    {
        var runner = new WindowsServiceServerRunner(options, runServer);

        // A bare HostBuilder, not Host.CreateDefaultBuilder: the shell hosts nothing that reads
        // configuration, and a service process starts in %SystemRoot%\System32, so the default
        // builder's content root and configuration file probing would only add surface.
        using IHost serviceHost = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddWindowsService(lifetimeOptions => lifetimeOptions.ServiceName = ServiceName);
                services.Configure<HostOptions>(hostOptions => hostOptions.ShutdownTimeout = _shutdownTimeout);
                services.AddSingleton<IHostedService>(runner);
            })
            .Build();

        runner.Attach(serviceHost.Services);
        await serviceHost.RunAsync().ConfigureAwait(false);
        return runner.ExitCode;
    }
}
