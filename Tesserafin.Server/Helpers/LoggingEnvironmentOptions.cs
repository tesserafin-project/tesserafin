using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Serilog.Events;

namespace Tesserafin.Server.Helpers;

/// <summary>
/// The two operator-facing logging knobs a container can be reconfigured with, resolved from the
/// application configuration (#91 / [A5]).
/// </summary>
/// <remarks>
/// <para>
/// There is one canonical mechanism and it is the one the server already had: application
/// configuration, into which <c>Program.CreateAppConfiguration</c> binds every environment
/// variable prefixed with <c>TESSERAFIN_</c>. So <see cref="LogLevelKey"/> is set by
/// <c>TESSERAFIN_LOG_LEVEL</c> and <see cref="LogFormatKey"/> by <c>TESSERAFIN_LOG_FORMAT</c>,
/// exactly like the pre-existing <c>TESSERAFIN_DATA_DIR</c> / <c>TESSERAFIN_CONFIG_DIR</c> family.
/// No second configuration system and no convenience aliases are introduced.
/// </para>
/// <para>
/// Both values are validated here rather than at the point of use, so an operator typo degrades to
/// the documented default and a structured warning instead of taking the server down. That warning
/// can only be emitted once a logger exists, which is why the invalid input is carried on this
/// object rather than thrown.
/// </para>
/// </remarks>
public sealed class LoggingEnvironmentOptions
{
    /// <summary>
    /// The configuration key holding the minimum log level. Set with <c>TESSERAFIN_LOG_LEVEL</c>.
    /// Accepted values are the Serilog level names: Verbose, Debug, Information, Warning, Error,
    /// Fatal.
    /// </summary>
    public const string LogLevelKey = "LOG_LEVEL";

    /// <summary>
    /// The configuration key selecting the console rendering. Set with
    /// <c>TESSERAFIN_LOG_FORMAT</c>. Accepted values are <c>json</c> and <c>text</c>.
    /// </summary>
    public const string LogFormatKey = "LOG_FORMAT";

    /// <summary>Machine-readable console output: one JSON object per line.</summary>
    public const string FormatJson = "json";

    /// <summary>Human-readable console output. The default outside the container image.</summary>
    public const string FormatText = "text";

    private LoggingEnvironmentOptions(
        bool useJsonConsole,
        string? minimumLevel,
        string? rejectedLevel,
        string? rejectedFormat)
    {
        UseJsonConsole = useJsonConsole;
        MinimumLevel = minimumLevel;
        RejectedLevel = rejectedLevel;
        RejectedFormat = rejectedFormat;
    }

    /// <summary>
    /// Gets the accepted values for <see cref="LogLevelKey"/>, in increasing severity.
    /// </summary>
    public static IReadOnlyList<string> ValidLevels { get; } = Enum.GetNames<LogEventLevel>();

    /// <summary>
    /// Gets a value indicating whether the console must emit one JSON object per line.
    /// </summary>
    public bool UseJsonConsole { get; }

    /// <summary>
    /// Gets the validated minimum level to force, or <see langword="null"/> to leave whatever
    /// <c>logging.json</c> configured untouched.
    /// </summary>
    public string? MinimumLevel { get; }

    /// <summary>
    /// Gets the <see cref="LogLevelKey"/> value that was supplied and rejected, if any.
    /// </summary>
    public string? RejectedLevel { get; }

    /// <summary>
    /// Gets the <see cref="LogFormatKey"/> value that was supplied and rejected, if any.
    /// </summary>
    public string? RejectedFormat { get; }

    /// <summary>
    /// Gets a value indicating whether any supplied value was rejected.
    /// </summary>
    public bool HasRejectedValues => RejectedLevel is not null || RejectedFormat is not null;

    /// <summary>
    /// Resolves the logging options from the application configuration.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The resolved options. Never throws: invalid input becomes a rejected value.</returns>
    public static LoggingEnvironmentOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rawLevel = configuration[LogLevelKey]?.Trim();
        string? minimumLevel = null;
        string? rejectedLevel = null;
        if (!string.IsNullOrEmpty(rawLevel))
        {
            var match = ValidLevels.FirstOrDefault(l => string.Equals(l, rawLevel, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                rejectedLevel = rawLevel;
            }
            else
            {
                minimumLevel = match;
            }
        }

        var rawFormat = configuration[LogFormatKey]?.Trim();
        var useJson = false;
        string? rejectedFormat = null;
        if (!string.IsNullOrEmpty(rawFormat))
        {
            if (string.Equals(rawFormat, FormatJson, StringComparison.OrdinalIgnoreCase))
            {
                useJson = true;
            }
            else if (!string.Equals(rawFormat, FormatText, StringComparison.OrdinalIgnoreCase))
            {
                rejectedFormat = rawFormat;
            }
        }

        return new LoggingEnvironmentOptions(useJson, minimumLevel, rejectedLevel, rejectedFormat);
    }
}
