using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Serilog.Formatting;
using Serilog.Templates;

namespace Tesserafin.Server.Helpers;

/// <summary>
/// The JSON-lines console rendering used by the distributable container image (#91 / [A5]).
/// </summary>
/// <remarks>
/// <para>
/// This is a rendering choice, not a second logging framework: the pipeline stays Serilog, the
/// minimum levels, per-source overrides and enrichers still come from <c>logging.json</c>, and
/// events keep their named properties. Only the console (and the rolling file, so the two agree)
/// is re-rendered.
/// </para>
/// <para>
/// The template is <see cref="ExpressionTemplate"/> from <c>Serilog.Expressions</c>, which the
/// server already references — no new package is taken on for this. Structured properties survive
/// as first-class JSON fields via <c>rest()</c>, so an event such as A4's hardware-selection
/// decision stays queryable by <c>Mode</c>, <c>Backend</c> and <c>Reason</c> instead of collapsing
/// into one opaque sentence.
/// </para>
/// </remarks>
public static class StructuredLogging
{
    /// <summary>
    /// One JSON object per line. Explicitly named fields come first; every remaining event
    /// property is spread in by <c>rest()</c>, which is what keeps structured events searchable.
    /// <c>exception</c> is produced by the formatter, never hand-assembled into the message.
    /// </summary>
    public const string JsonLineTemplate =
        "{ {timestamp: @t, level: if @l = undefined then 'Information' else @l, message: @m, sourceContext: SourceContext, threadId: ThreadId, exception: @x, ..rest()} }\n";

    /// <summary>
    /// Creates the JSON-lines formatter.
    /// </summary>
    /// <returns>A formatter emitting exactly one valid JSON object per log event, newline terminated.</returns>
    public static ITextFormatter CreateJsonFormatter()
        => new ExpressionTemplate(JsonLineTemplate, formatProvider: CultureInfo.InvariantCulture);

    /// <summary>
    /// Produces the configuration Serilog is initialised from, applying the operator overrides.
    /// </summary>
    /// <param name="source">The application configuration.</param>
    /// <param name="options">The resolved logging options.</param>
    /// <param name="dropConfiguredSinks">
    /// When <see langword="true"/>, every <c>Serilog:WriteTo</c> key is withheld so the caller can
    /// install its own sinks while still honouring the operator's <c>MinimumLevel</c>,
    /// <c>Override</c> and <c>Enrich</c> settings. Used by the JSON branch, which owns the
    /// rendering and therefore cannot let the file's text console sink through: a single text line
    /// on stdout would break the "every line is JSON" contract.
    /// </param>
    /// <returns>The effective configuration for <c>ReadFrom.Configuration</c>.</returns>
    public static IConfiguration BuildSerilogConfiguration(
        IConfiguration source,
        LoggingEnvironmentOptions options,
        bool dropConfiguredSinks)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (options.MinimumLevel is not null)
        {
            // Cleared as well as re-set: `logging.json` may express the minimum level either as a
            // scalar (`"MinimumLevel": "Debug"`) or as an object with a `Default` child. Writing
            // only the child would leave a stale scalar shadowing it.
            overrides["Serilog:MinimumLevel"] = null;
            overrides["Serilog:MinimumLevel:Default"] = options.MinimumLevel;
        }

        if (!dropConfiguredSinks && overrides.Count == 0)
        {
            return source;
        }

        var builder = new ConfigurationBuilder();
        if (dropConfiguredSinks)
        {
            var kept = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in source.AsEnumerable())
            {
                if (entry.Key.StartsWith("Serilog:WriteTo", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                kept[entry.Key] = entry.Value;
            }

            builder.AddInMemoryCollection(kept);
        }
        else
        {
            builder.AddConfiguration(source);
        }

        builder.AddInMemoryCollection(overrides);
        return builder.Build();
    }
}
