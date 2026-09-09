using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Tesserafin.Server.ServiceHost;

/// <summary>
/// The single hosted service of the Windows service shell: it starts the real server, reports its
/// outcome to the Service Control Manager, and turns an SCM stop into a server shutdown.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceServerRunner : IHostedService
{
    private readonly StartupOptions _options;
    private readonly Func<StartupOptions, Task<int>> _runServer;

    private IServiceProvider? _services;
    private Task<int>? _serverTask;
    private int _exitCode;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsServiceServerRunner"/> class.
    /// </summary>
    /// <param name="options">The parsed startup options.</param>
    /// <param name="runServer">The server entry point, which returns its exit code.</param>
    internal WindowsServiceServerRunner(StartupOptions options, Func<StartupOptions, Task<int>> runServer)
    {
        _options = options;
        _runServer = runServer;
    }

    /// <summary>
    /// Gets the exit code the process should report.
    /// </summary>
    internal int ExitCode => _exitCode;

    /// <summary>
    /// Supplies the shell host's services once it has been built.
    /// </summary>
    /// <param name="services">The shell host's service provider.</param>
    internal void Attach(IServiceProvider services) => _services = services;

    /// <inheritdoc />
    /// <remarks>
    /// Returns as soon as the server has been handed to the thread pool. By the time this runs the
    /// SCM handshake has already happened — <c>Host.StartAsync</c> awaits
    /// <c>IHostLifetime.WaitForStartAsync</c> first — and <c>OnStart</c> has returned, so the
    /// service is already <c>RUNNING</c>. Awaiting the server here instead would put database
    /// migrations inside the SCM's start timeout, which is the failure this design exists to avoid.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serverTask = Task.Run(() => _runServer(_options), CancellationToken.None);
        _ = ObserveServerAsync(_serverTask);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// An SCM stop arrives here. The server is asked to shut down and then awaited, so the service
    /// does not report <c>STOPPED</c> while its own process is still writing to the database.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Program.RequestShutdown();

        if (_serverTask is null)
        {
            return;
        }

        try
        {
            _exitCode = await _serverTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The server logs its own failures; the contract owed here is the code, not a rethrow
            // that would replace a reportable exit with an unhandled exception in the shell.
            _exitCode = Program.StartupFailureExitCode;
        }

        PublishExitCode(_exitCode);
    }

    /// <summary>
    /// Watches for the server ending on its own — a fatal startup, or an in-process restart loop
    /// running out — and takes the service down with it rather than leaving a service reported
    /// <c>RUNNING</c> with nothing behind it.
    /// </summary>
    /// <param name="serverTask">The running server.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task ObserveServerAsync(Task<int> serverTask)
    {
        try
        {
            _exitCode = await serverTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            _exitCode = Program.StartupFailureExitCode;
        }

        // Order matters. ServiceBase reports its ExitCode to the SCM when the service stops, so the
        // code has to be in place before the stop is requested.
        PublishExitCode(_exitCode);
        _services?.GetService<IHostApplicationLifetime>()?.StopApplication();
    }

    /// <summary>
    /// Puts a non-zero exit code where the Service Control Manager will read it.
    /// </summary>
    /// <param name="exitCode">The exit code.</param>
    /// <remarks>
    /// Two channels, because the SCM uses two. If the process dies without reporting
    /// <c>SERVICE_STOPPED</c> the SCM records its own <c>1067</c> and the process exit code is what
    /// an operator or a script sees; if the shell stops cleanly the SCM reads
    /// <c>ServiceBase.ExitCode</c>, which defaults to <c>0</c> and would otherwise report exactly
    /// the "stopped normally" that W0 §2.5 names as the defect. Setting both is what makes a fatal
    /// startup non-zero in either shape.
    /// </remarks>
    private void PublishExitCode(int exitCode)
    {
        Environment.ExitCode = exitCode;

        if (exitCode != 0 && _services?.GetService<IHostLifetime>() is WindowsServiceLifetime lifetime)
        {
            lifetime.ExitCode = exitCode;
        }
    }
}
