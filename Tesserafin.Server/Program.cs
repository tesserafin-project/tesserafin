using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Net;
using Tesserafin.Controller;
using Tesserafin.Database.Implementations;
using Tesserafin.Server.Core;
using Tesserafin.Server.Core.Configuration;
using Tesserafin.Server.Core.Serialization;
using Tesserafin.Server.Extensions;
using Tesserafin.Server.Helpers;
using Tesserafin.Server.Implementations.DatabaseConfiguration;
using Tesserafin.Server.Implementations.Extensions;
using Tesserafin.Server.Implementations.StorageHelpers;
using Tesserafin.Server.Implementations.SystemBackupService;
using Tesserafin.Server.Migrations;
using Tesserafin.Server.Migrations.Stages;
using Tesserafin.Server.ServerSetupApp;
using Tesserafin.Server.ServiceHost;
using static Tesserafin.Controller.Extensions.ConfigurationExtensions;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Tesserafin.Server
{
    /// <summary>
    /// Class containing the entry point of the application.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// The name of logging configuration file containing application defaults.
        /// </summary>
        public const string LoggingConfigFileDefault = "logging.default.json";

        /// <summary>
        /// The name of the logging configuration file containing the system-specific override settings.
        /// </summary>
        public const string LoggingConfigFileSystem = "logging.json";

        /// <summary>
        /// The process exit code reported when startup fails fatally.
        /// </summary>
        /// <remarks>
        /// W0 §2.5 measured the unmodified server exiting <c>0</c> after
        /// <see cref="Common.FfmpegException"/> killed startup, which the Windows Service Control
        /// Manager reads as a service that stopped normally: no failure action fires and nothing
        /// records that the server never came up. Every fatal startup path therefore has to leave a
        /// non-zero code behind.
        /// </remarks>
        internal const int StartupFailureExitCode = 1;

        private static readonly SerilogLoggerFactory _loggerFactory = new SerilogLoggerFactory();

        /// <summary>
        /// Cancelled when something outside the server asks it to shut down — currently only the
        /// Windows Service Control Manager, by way of <see cref="WindowsServiceServerRunner"/>.
        /// </summary>
        /// <remarks>
        /// A stop can arrive while the server is still applying database migrations, long before
        /// <see cref="_reefinHost"/> exists. Holding the request in a token rather than reaching for
        /// the host means the registration made in <see cref="StartServer"/> fires immediately when
        /// the request already arrived, instead of the stop being silently dropped.
        /// </remarks>
        private static readonly CancellationTokenSource _serviceShutdownRequested = new();

        private static SetupServer? _setupServer;
        private static CoreAppHost? _appHost;
        private static IHost? _reefinHost = null;
        private static long _startTimestamp;
        private static ILogger _logger = NullLogger.Instance;
        private static bool _restartOnShutdown;
        private static IStartupLogger<TesserafinMigrationService>? _migrationLogger;
        private static string? _restoreFromBackup;

        /// <summary>
        /// The entry point of the application.
        /// </summary>
        /// <param name="args">The command line arguments passed.</param>
        /// <returns><see cref="Task" />.</returns>
        public static Task Main(string[] args)
        {
            static Task ErrorParsingArguments(IEnumerable<Error> errors)
            {
                Environment.ExitCode = 1;
                return Task.CompletedTask;
            }

            // Parse the command line arguments and either start the app or exit indicating error
            return Parser.Default.ParseArguments<StartupOptions>(args)
                .MapResult(StartApp, ErrorParsingArguments);
        }

        /// <summary>
        /// Asks the running server to shut down on behalf of something that is not the console
        /// lifetime.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="WindowsServiceServerRunner"/> when the Service Control Manager sends
        /// a stop. It is deliberately request-shaped and does not wait: the caller owns the timeout,
        /// because only the caller knows what the SCM is prepared to wait for.
        /// </remarks>
        internal static void RequestShutdown()
        {
            if (!_serviceShutdownRequested.IsCancellationRequested)
            {
                _serviceShutdownRequested.Cancel();
            }
        }

        private static async Task StartApp(StartupOptions options)
        {
            // `--service` is the operator's declaration and `IsWindowsService()` is the environment's
            // answer; both are required. The framework's own AddWindowsService is inert unless the
            // process really is an SCM service process, so demanding the flag as well is what keeps
            // `tesserafin --service` from a console byte-for-byte the behaviour it has today.
            int exitCode;
            if (OperatingSystem.IsWindows() && options.IsService && WindowsServiceHelpers.IsWindowsService())
            {
                exitCode = await WindowsServiceEntryPoint.RunAsync(options, RunServerAsync).ConfigureAwait(false);
            }
            else
            {
                exitCode = await RunServerAsync(options).ConfigureAwait(false);
            }

            if (exitCode != 0)
            {
                // Environment.ExitCode alone is a promise kept only if nothing else keeps the
                // process alive. A fatal startup has already torn down the host, so exiting here
                // makes the non-zero code a fact rather than an intention. A clean stop returns
                // normally and keeps the existing exit 0.
                Environment.ExitCode = exitCode;
                Environment.Exit(exitCode);
            }
        }

        private static async Task<int> RunServerAsync(StartupOptions options)
        {
            _restoreFromBackup = options.RestoreArchive;
            _startTimestamp = Stopwatch.GetTimestamp();
            ServerApplicationPaths appPaths = StartupHelpers.CreateApplicationPaths(options);
            appPaths.MakeSanityCheckOrThrow();

            // $TESSERAFIN_LOG_DIR needs to be set for the logger configuration manager
            Environment.SetEnvironmentVariable("TESSERAFIN_LOG_DIR", appPaths.LogDirectoryPath);

            // Enable cl-va P010 interop for tonemapping on Intel VAAPI
            Environment.SetEnvironmentVariable("NEOReadDebugKeys", "1");
            Environment.SetEnvironmentVariable("EnableExtendedVaFormats", "1");

            await StartupHelpers.InitLoggingConfigFile(appPaths).ConfigureAwait(false);

            // Create an instance of the application configuration to use for application startup
            IConfiguration startupConfig = CreateAppConfiguration(options, appPaths);
            StartupHelpers.InitializeLoggingFramework(startupConfig, appPaths);
            _setupServer = new SetupServer(static () => _reefinHost?.Services?.GetService<INetworkManager>(), appPaths, static () => _appHost, _loggerFactory, startupConfig);
            await _setupServer.RunAsync().ConfigureAwait(false);
            _logger = _loggerFactory.CreateLogger("Main");
            StartupLogger.Logger = new StartupLogger(_logger);

            // Use the logging framework for uncaught exceptions instead of std error
            AppDomain.CurrentDomain.UnhandledException += (_, e)
                => _logger.LogCritical((Exception)e.ExceptionObject, "Unhandled Exception");

            _logger.LogInformation(
                "Tesserafin version: {Version}",
                Assembly.GetEntryAssembly()!.GetName().Version!.ToString(3));

            StartupHelpers.LogEnvironmentInfo(_logger, appPaths);

            // If hosting the web client, validate the client content path
            if (startupConfig.HostWebClient())
            {
                var webContentPath = appPaths.WebPath;
                if (!Directory.Exists(webContentPath) || !Directory.EnumerateFiles(webContentPath).Any())
                {
                    _logger.LogError(
                        "The server is expected to host the web client, but the provided content directory is either " +
                        "invalid or empty: {WebContentPath}. If you do not want to host the web client with the " +
                        "server, you may set the '--nowebclient' command line flag, or set" +
                        "'{ConfigKey}=false' in your config settings",
                        webContentPath,
                        HostWebClientKey);
                    return StartupFailureExitCode;
                }
            }

            SetupServer.ReportActivity(StartupActivity.CheckingStorage);
            StorageHelper.TestCommonPathsForStorageCapacity(appPaths, StartupLogger.Logger.With(_loggerFactory.CreateLogger<Startup>()).BeginGroup($"Storage Check"));

            StartupHelpers.PerformStaticInitialization();

            SetupServer.ReportActivity(StartupActivity.Initializing);
            await ApplyStartupMigrationAsync(appPaths, startupConfig, options).ConfigureAwait(false);

            int exitCode;
            do
            {
                exitCode = await StartServer(appPaths, options, startupConfig).ConfigureAwait(false);

                if (_restartOnShutdown)
                {
                    _startTimestamp = Stopwatch.GetTimestamp();
                    await _setupServer.StopAsync().ConfigureAwait(false);
                    await _setupServer.RunAsync().ConfigureAwait(false);
                }
            } while (_restartOnShutdown);

            _setupServer.Dispose();
            return exitCode;
        }

        private static async Task<int> StartServer(IServerApplicationPaths appPaths, StartupOptions options, IConfiguration startupConfig)
        {
            using CoreAppHost appHost = new CoreAppHost(
                            appPaths,
                            _loggerFactory,
                            options,
                            startupConfig);
            var configurationCompleted = false;
            var exitCode = 0;
            CancellationTokenRegistration shutdownRegistration = default;
            try
            {
                _reefinHost = Host.CreateDefaultBuilder()
                    .UseConsoleLifetime()
                    .ConfigureServices(services => appHost.Init(services))
                    .ConfigureWebHostDefaults(webHostBuilder =>
                    {
                        webHostBuilder.ConfigureWebHostBuilder(appHost, startupConfig, appPaths, _logger);
                        if (bool.TryParse(Environment.GetEnvironmentVariable("TESSERAFIN_ENABLE_IIS"), out var iisEnabled) && iisEnabled)
                        {
                            _logger.LogCritical("UNSUPPORTED HOSTING ENVIRONMENT Microsoft Internet Information Services. The option to run Tesserafin on IIS is an unsupported and untested feature. Only use at your own discretion.");
                            webHostBuilder.UseIIS();
                        }
                    })
                    .ConfigureAppConfiguration(config => config.ConfigureAppConfiguration(options, appPaths, startupConfig))
                    .UseSerilog()
                    .ConfigureServices(e => e
                        .RegisterStartupLogger()
                        .AddSingleton<IServiceCollection>(e))
                    .Build();

                // Registering after Build() rather than reaching for `_reefinHost` from
                // `RequestShutdown` closes the window in which a stop arrives while the server is
                // still migrating: an already-cancelled token runs its callback inline here, so the
                // request cannot be dropped on the floor because the host did not exist yet.
                shutdownRegistration = _serviceShutdownRequested.Token.Register(
                    static () => _reefinHost?.Services.GetService<IHostApplicationLifetime>()?.StopApplication());

                /*
                 * Initialize the transcode path marker so we avoid starting Tesserafin in a broken state.
                 * This should really be a part of IApplicationPaths but this path is configured differently.
                 */
                _ = appHost.ConfigurationManager.GetTranscodePath();

                // Re-use the host service provider in the app host since ASP.NET doesn't allow a custom service collection.
                appHost.ServiceProvider = _reefinHost.Services;
                PrepareDatabaseProvider(appHost.ServiceProvider);

                if (!string.IsNullOrWhiteSpace(_restoreFromBackup))
                {
                    SetupServer.ReportActivity(StartupActivity.RestoringBackup);
                    await appHost.ServiceProvider.GetService<IBackupService>()!.RestoreBackupAsync(_restoreFromBackup).ConfigureAwait(false);
                    _restoreFromBackup = null;
                    _restartOnShutdown = true;
                    return exitCode;
                }

                var reefinMigrationService = ActivatorUtilities.CreateInstance<TesserafinMigrationService>(appHost.ServiceProvider);
                SetupServer.ReportActivity(StartupActivity.PreparingMigrations);
                await reefinMigrationService.PrepareSystemForMigration(_logger).ConfigureAwait(false);
                // "Preparing migrations" carries through the DB read; per-migration progress is reported
                // as "Running migration X of Y" from inside the step once the pending set is known.
                await reefinMigrationService.MigrateStepAsync(TesserafinMigrationStageTypes.CoreInitialisation, appHost.ServiceProvider).ConfigureAwait(false);

                SetupServer.ReportActivity(StartupActivity.InitializingServices);
                await appHost.InitializeServices(startupConfig).ConfigureAwait(false);
                _appHost = appHost;

                await reefinMigrationService.MigrateStepAsync(TesserafinMigrationStageTypes.AppInitialisation, appHost.ServiceProvider).ConfigureAwait(false);
                await reefinMigrationService.CleanupSystemAfterMigration(_logger).ConfigureAwait(false);
                try
                {
                    configurationCompleted = true;
                    await _setupServer!.StopAsync().ConfigureAwait(false);

                    if (options.StartupMode is null or Configuration.StartupMode.MediaServer)
                    {
                        await _reefinHost.StartAsync().ConfigureAwait(false);

                        if (!OperatingSystem.IsWindows() && startupConfig.UseUnixSocket())
                        {
                            var socketPath = StartupHelpers.GetUnixSocketPath(startupConfig, appPaths);

                            StartupHelpers.SetUnixSocketPermissions(startupConfig, socketPath, _logger);
                        }
                    }
                }
                catch (Exception)
                {
                    _logger.LogError("Kestrel failed to start! This is most likely due to an invalid address or port bind - correct your bind configuration in network.xml and try again");
                    throw;
                }

                if (options.StartupMode is null or Configuration.StartupMode.MediaServer)
                {
                    await appHost.RunStartupTasksAsync().ConfigureAwait(false);
                    _logger.LogInformation("Startup complete {Time:g}", Stopwatch.GetElapsedTime(_startTimestamp));

                    await _reefinHost.WaitForShutdownAsync().ConfigureAwait(false);
                }

                _restartOnShutdown = appHost.ShouldRestart;
                _restoreFromBackup = appHost.RestoreBackupPath;
            }
            catch (Exception ex)
            {
                _restartOnShutdown = false;
                _logger.LogCritical(ex, "Error while starting server");

                // W0 §2.5: reaching here and returning 0 is what tells the SCM the service stopped
                // normally after `FfmpegException` killed startup. This is the exit contract W3
                // owes, and it deliberately covers every fatal startup exception rather than
                // special-casing the encoder — a server that could not start is a failed start
                // whatever killed it. The clean-shutdown path never runs this block, so a console
                // Ctrl+C after a successful start still exits 0.
                exitCode = StartupFailureExitCode;
                if (_setupServer!.IsAlive && !configurationCompleted)
                {
                    _setupServer!.SoftStop();
                    if (options.StartupMode is null or Configuration.StartupMode.MediaServer)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                    }

                    await _setupServer!.StopAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // Don't throw additional exception if startup failed.
                if (appHost.ServiceProvider is not null)
                {
                    _logger.LogInformation("Running query planner optimizations in the database... This might take a while");

                    var databaseProvider = appHost.ServiceProvider.GetRequiredService<ITesserafinDatabaseProvider>();
                    using var shutdownSource = new CancellationTokenSource();
                    shutdownSource.CancelAfter((int)TimeSpan.FromSeconds(60).TotalMicroseconds);
                    await databaseProvider.RunShutdownTask(shutdownSource.Token).ConfigureAwait(false);
                }

                _appHost = null;
                _reefinHost?.Dispose();
                _reefinHost = null;
                await shutdownRegistration.DisposeAsync().ConfigureAwait(false);
            }

            return exitCode;
        }

        /// <summary>
        /// [Internal]Runs the startup Migrations.
        /// </summary>
        /// <remarks>
        /// Not intended to be used other then by reefin and its tests.
        /// </remarks>
        /// <param name="appPaths">Application Paths.</param>
        /// <param name="startupConfig">Startup Config.</param>
        /// <param name="startupOptions">The applications startup options.</param>
        /// <returns>A task.</returns>
        public static async Task ApplyStartupMigrationAsync(ServerApplicationPaths appPaths, IConfiguration startupConfig, StartupOptions startupOptions)
        {
            _migrationLogger = StartupLogger.Logger.BeginGroup<TesserafinMigrationService>($"Migration Service");
            var startupConfigurationManager = new ServerConfigurationManager(appPaths, _loggerFactory, new MyXmlSerializer());
            startupConfigurationManager.AddParts([new DatabaseConfigurationFactory()]);
            var migrationStartupServiceProvider = new ServiceCollection()
                .AddLogging(d => d.AddSerilog())
                .AddTesserafinDbContext(startupConfigurationManager, startupConfig)
                .AddSingleton<IApplicationPaths>(appPaths)
                .AddSingleton<ServerApplicationPaths>(appPaths)
                .RegisterStartupLogger();

            migrationStartupServiceProvider.AddSingleton(migrationStartupServiceProvider);
            var startupService = migrationStartupServiceProvider.BuildServiceProvider();

            PrepareDatabaseProvider(startupService);

            var reefinMigrationService = ActivatorUtilities.CreateInstance<TesserafinMigrationService>(startupService);
            await reefinMigrationService.CheckFirstTimeRunOrMigration(appPaths, startupOptions).ConfigureAwait(false);
            await reefinMigrationService.MigrateStepAsync(Migrations.Stages.TesserafinMigrationStageTypes.PreInitialisation, startupService).ConfigureAwait(false);
        }

        /// <summary>
        /// [Internal]Runs the Tesserafin migrator service with the Core stage.
        /// </summary>
        /// <remarks>
        /// Not intended to be used other then by reefin and its tests.
        /// </remarks>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="reefinMigrationStage">The stage to run.</param>
        /// <returns>A task.</returns>
        public static async Task ApplyCoreMigrationsAsync(IServiceProvider serviceProvider, Migrations.Stages.TesserafinMigrationStageTypes reefinMigrationStage)
        {
            var reefinMigrationService = ActivatorUtilities.CreateInstance<TesserafinMigrationService>(serviceProvider, _migrationLogger!);
            await reefinMigrationService.MigrateStepAsync(reefinMigrationStage, serviceProvider).ConfigureAwait(false);
        }

        /// <summary>
        /// Create the application configuration.
        /// </summary>
        /// <param name="commandLineOpts">The command line options passed to the program.</param>
        /// <param name="appPaths">The application paths.</param>
        /// <returns>The application configuration.</returns>
        public static IConfiguration CreateAppConfiguration(StartupOptions commandLineOpts, IApplicationPaths appPaths)
        {
            return new ConfigurationBuilder()
                .ConfigureAppConfiguration(commandLineOpts, appPaths)
                .Build();
        }

        private static IConfigurationBuilder ConfigureAppConfiguration(
            this IConfigurationBuilder config,
            StartupOptions commandLineOpts,
            IApplicationPaths appPaths,
            IConfiguration? startupConfig = null)
        {
            // Use the swagger API page as the default redirect path if not hosting the web client
            var inMemoryDefaultConfig = ConfigurationOptions.DefaultConfiguration;
            if (startupConfig is not null && !startupConfig.HostWebClient())
            {
                inMemoryDefaultConfig[DefaultRedirectKey] = "api-docs/swagger";
            }

            return config
                .SetBasePath(appPaths.ConfigurationDirectoryPath)
                .AddInMemoryCollection(inMemoryDefaultConfig)
                .AddJsonFile(LoggingConfigFileDefault, optional: false, reloadOnChange: true)
                .AddJsonFile(LoggingConfigFileSystem, optional: true, reloadOnChange: true)
                .AddEnvironmentVariables("TESSERAFIN_")
                .AddInMemoryCollection(commandLineOpts.ConvertToConfig());
        }

        private static void PrepareDatabaseProvider(IServiceProvider services)
        {
            var factory = services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
            var provider = services.GetRequiredService<ITesserafinDatabaseProvider>();
            provider.DbContextFactory = factory;
        }
    }
}
