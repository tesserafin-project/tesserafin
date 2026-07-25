using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Extensions;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core;
using Tesserafin.Server.ServerSetupApp;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Tesserafin.Server.Helpers;

/// <summary>
/// A class containing helper methods for server startup.
/// </summary>
public static class StartupHelpers
{
    private static readonly string[] _relevantEnvVarPrefixes = { "TESSERAFIN_", "DOTNET_", "ASPNETCORE_" };

    /// <summary>
    /// Logs relevant environment variables and information about the host.
    /// </summary>
    /// <param name="logger">The logger to use.</param>
    /// <param name="appPaths">The application paths to use.</param>
    public static void LogEnvironmentInfo(ILogger logger, IApplicationPaths appPaths)
    {
        // Distinct these to prevent users from reporting problems that aren't actually problems
        var commandLineArgs = Environment
            .GetCommandLineArgs()
            .Distinct();

        // Get all relevant environment variables
        var allEnvVars = Environment.GetEnvironmentVariables();
        var relevantEnvVars = new Dictionary<object, object>();
        foreach (var key in allEnvVars.Keys)
        {
            if (_relevantEnvVarPrefixes.Any(prefix => key.ToString()!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                relevantEnvVars.Add(key, allEnvVars[key]!);
            }
        }

        logger.LogInformation("Environment Variables: {EnvVars}", relevantEnvVars);
        logger.LogInformation("Arguments: {Args}", commandLineArgs);
        logger.LogInformation("Operating system: {OS}", RuntimeInformation.OSDescription);
        logger.LogInformation("Architecture: {Architecture}", RuntimeInformation.OSArchitecture);
        logger.LogInformation("64-Bit Process: {Is64Bit}", Environment.Is64BitProcess);
        logger.LogInformation("User Interactive: {IsUserInteractive}", Environment.UserInteractive);
        logger.LogInformation("Processor count: {ProcessorCount}", Environment.ProcessorCount);
        logger.LogInformation("Program data path: {ProgramDataPath}", appPaths.ProgramDataPath);
        logger.LogInformation("Log directory path: {LogDirectoryPath}", appPaths.LogDirectoryPath);
        logger.LogInformation("Config directory path: {ConfigurationDirectoryPath}", appPaths.ConfigurationDirectoryPath);
        logger.LogInformation("Cache path: {CachePath}", appPaths.CachePath);
        logger.LogInformation("Temp directory path: {TempDirPath}", appPaths.TempDirectory);
        logger.LogInformation("Web resources path: {WebPath}", appPaths.WebPath);
        logger.LogInformation("Application directory: {ApplicationPath}", appPaths.ProgramSystemPath);
    }

    /// <summary>
    /// Create the data, config and log paths from the variety of inputs(command line args,
    /// environment variables) or decide on what default to use. For Windows it's %AppPath%
    /// for everything else the
    /// <a href="https://specifications.freedesktop.org/basedir-spec/basedir-spec-latest.html">XDG approach</a>
    /// is followed.
    /// </summary>
    /// <param name="options">The <see cref="StartupOptions" /> for this instance.</param>
    /// <returns><see cref="ServerApplicationPaths" />.</returns>
    public static ServerApplicationPaths CreateApplicationPaths(StartupOptions options)
    {
        // LocalApplicationData
        // Windows: %LocalAppData%
        // macOS: NSApplicationSupportDirectory
        // UNIX: $XDG_DATA_HOME
        var dataDir = options.DataDir
            ?? Environment.GetEnvironmentVariable("TESSERAFIN_DATA_DIR")
            ?? Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                "tesserafin");

        var configDir = options.ConfigDir ?? Environment.GetEnvironmentVariable("TESSERAFIN_CONFIG_DIR");
        if (configDir is null)
        {
            configDir = Path.Join(dataDir, "config");
            if (options.DataDir is null
                && !Directory.Exists(configDir)
                && !OperatingSystem.IsWindows()
                && !OperatingSystem.IsMacOS())
            {
                // UNIX: $XDG_CONFIG_HOME
                configDir = Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                    "tesserafin");
            }
        }

        var cacheDir = options.CacheDir ?? Environment.GetEnvironmentVariable("TESSERAFIN_CACHE_DIR");
        if (cacheDir is null)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                cacheDir = Path.Join(dataDir, "cache");
            }
            else
            {
                cacheDir = Path.Join(GetXdgCacheHome(), "tesserafin");
            }
        }

        var webDir = options.WebDir ?? Environment.GetEnvironmentVariable("TESSERAFIN_WEB_DIR");
        if (webDir is null)
        {
            webDir = Path.Join(AppContext.BaseDirectory, "jellyfin-web");
        }

        var logDir = options.LogDir ?? Environment.GetEnvironmentVariable("TESSERAFIN_LOG_DIR");
        if (logDir is null)
        {
            logDir = Path.Join(dataDir, "log");
        }

        // Normalize paths. Only possible with GetFullPath for now - https://github.com/dotnet/runtime/issues/2162
        dataDir = Path.GetFullPath(dataDir);
        logDir = Path.GetFullPath(logDir);
        configDir = Path.GetFullPath(configDir);
        cacheDir = Path.GetFullPath(cacheDir);
        webDir = Path.GetFullPath(webDir);

        // Ensure the main folders exist before we continue
        try
        {
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(logDir);
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(cacheDir);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("Error whilst attempting to create folder");
            Console.Error.WriteLine(ex.ToString());
            Environment.Exit(1);
        }

        return new ServerApplicationPaths(dataDir, logDir, configDir, cacheDir, webDir);
    }

    private static string GetXdgCacheHome()
    {
        // $XDG_CACHE_HOME defines the base directory relative to which
        // user specific non-essential data files should be stored.
        var cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

        // If $XDG_CACHE_HOME is either not set or a relative path,
        // a default equal to $HOME/.cache should be used.
        if (cacheHome is null || !cacheHome.StartsWith('/'))
        {
            cacheHome = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
                ".cache");
        }

        return cacheHome;
    }

    /// <summary>
    /// Gets the path for the unix socket Kestrel should bind to.
    /// </summary>
    /// <param name="startupConfig">The startup config.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <returns>The path for Kestrel to bind to.</returns>
    public static string GetUnixSocketPath(IConfiguration startupConfig, IApplicationPaths appPaths)
    {
        var socketPath = startupConfig.GetUnixSocketPath();

        if (string.IsNullOrEmpty(socketPath))
        {
            const string SocketFile = "tesserafin.sock";

            var xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (xdgRuntimeDir is null)
            {
                // Fall back to config dir
                socketPath = Path.Join(appPaths.ConfigurationDirectoryPath, SocketFile);
            }
            else
            {
                socketPath = Path.Join(xdgRuntimeDir, SocketFile);
            }
        }

        return socketPath;
    }

    /// <summary>
    /// Sets the unix file permissions for Kestrel's socket file.
    /// </summary>
    /// <param name="startupConfig">The startup config.</param>
    /// <param name="socketPath">The socket path.</param>
    /// <param name="logger">The logger.</param>
    [UnsupportedOSPlatform("windows")]
    public static void SetUnixSocketPermissions(IConfiguration startupConfig, string socketPath, ILogger logger)
    {
        var socketPerms = startupConfig.GetUnixSocketPermissions();

        if (!string.IsNullOrEmpty(socketPerms))
        {
            File.SetUnixFileMode(socketPath, (UnixFileMode)Convert.ToInt32(socketPerms, 8));
            logger.LogInformation("Kestrel unix socket permissions set to {SocketPerms}", socketPerms);
        }
    }

    /// <summary>
    /// Initialize the logging configuration file using the bundled resource file as a default if it doesn't exist
    /// already.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <returns>A task representing the creation of the configuration file, or a completed task if the file already exists.</returns>
    public static async Task InitLoggingConfigFile(IApplicationPaths appPaths)
    {
        // Do nothing if the config file already exists
        string configPath = Path.Combine(appPaths.ConfigurationDirectoryPath, Program.LoggingConfigFileDefault);
        if (File.Exists(configPath))
        {
            return;
        }

        // Get a stream of the resource contents
        // NOTE: The .csproj name is used instead of the assembly name in the resource path
        const string ResourcePath = "Tesserafin.Server.Resources.Configuration.logging.json";
        Stream resource = typeof(Program).Assembly.GetManifestResourceStream(ResourcePath)
                          ?? throw new InvalidOperationException($"Invalid resource path: '{ResourcePath}'");
        await using (resource.ConfigureAwait(false))
        {
            Stream dst = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
            await using (dst.ConfigureAwait(false))
            {
                // Copy the resource contents to the expected file path for the config file
                await resource.CopyToAsync(dst).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Initialize Serilog using configuration and fall back to defaults on failure.
    /// </summary>
    /// <param name="configuration">The configuration object.</param>
    /// <param name="appPaths">The application paths.</param>
    public static void InitializeLoggingFramework(IConfiguration configuration, IApplicationPaths appPaths)
    {
        // #91 / [A5]. Resolved before the try so an invalid value can be reported through whichever
        // logger ends up being installed, including the fallback one below.
        var loggingOptions = LoggingEnvironmentOptions.Read(configuration);

        try
        {
            var startupLogger = new LoggerProviderCollection();
            startupLogger.AddProvider(new SetupServer.SetupLoggerFactory());

            var loggerConfiguration = new LoggerConfiguration();
            if (loggingOptions.UseJsonConsole)
            {
                // Container mode: stdout IS the log transport, so it has to be machine readable and
                // every line has to be JSON. The sinks are therefore installed here instead of
                // being read from logging.json; everything else about the pipeline (minimum level,
                // per-source overrides, enrichers) still comes from the operator's file.
                var jsonFormatter = StructuredLogging.CreateJsonFormatter();
                loggerConfiguration
                    .ReadFrom.Configuration(StructuredLogging.BuildSerilogConfiguration(configuration, loggingOptions, dropConfiguredSinks: true))
                    .WriteTo.Console(jsonFormatter)
                    .WriteTo.Async(x => x.File(
                        StructuredLogging.CreateJsonFormatter(),
                        Path.Combine(appPaths.LogDirectoryPath, "log_.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 3,
                        rollOnFileSizeLimit: true,
                        fileSizeLimitBytes: 100000000,
                        encoding: Encoding.UTF8));
            }
            else
            {
                loggerConfiguration
                    .ReadFrom.Configuration(StructuredLogging.BuildSerilogConfiguration(configuration, loggingOptions, dropConfiguredSinks: false));
            }

            // Serilog.Log is used by SerilogLoggerFactory when no logger is specified.
            // The provider sink below feeds the startup wizard's /startup/logger route and must
            // stay wired in BOTH branches.
            Log.Logger = loggerConfiguration
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .WriteTo.Async(e => e.Providers(startupLogger))
                .CreateLogger();
        }
        catch (Exception ex)
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss}] [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    formatProvider: CultureInfo.InvariantCulture)
                .WriteTo.Async(x => x.File(
                    Path.Combine(appPaths.LogDirectoryPath, "log_.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{ThreadId}] {SourceContext}: {Message}{NewLine}{Exception}",
                    formatProvider: CultureInfo.InvariantCulture,
                    encoding: Encoding.UTF8))
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .CreateLogger();

            Log.Logger.Fatal(ex, "Failed to create/read logger configuration");
        }

        // A typo in an operator-supplied value must never take the server down, and must never be
        // silent either. Emitted through the logger that was actually installed, so in the
        // container it is itself a valid JSON line.
        if (loggingOptions.RejectedLevel is not null)
        {
            Log.Logger.Warning(
                "Ignoring invalid {ConfigurationKey} value {RejectedLogLevel}; keeping the configured minimum level. Valid values: {ValidLogLevels}",
                LoggingEnvironmentOptions.LogLevelKey,
                loggingOptions.RejectedLevel,
                LoggingEnvironmentOptions.ValidLevels);
        }

        if (loggingOptions.RejectedFormat is not null)
        {
            Log.Logger.Warning(
                "Ignoring invalid {ConfigurationKey} value {RejectedLogFormat}; falling back to {EffectiveLogFormat}",
                LoggingEnvironmentOptions.LogFormatKey,
                loggingOptions.RejectedFormat,
                LoggingEnvironmentOptions.FormatText);
        }
    }

    /// <summary>
    /// Call static initialization methods for the application.
    /// </summary>
    public static void PerformStaticInitialization()
    {
        // Make sure we have all the code pages we can get
        // Ref: https://docs.microsoft.com/en-us/dotnet/api/system.text.codepagesencodingprovider.instance?view=netcore-3.0#remarks
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
