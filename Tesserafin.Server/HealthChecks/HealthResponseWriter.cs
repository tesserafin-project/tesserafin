using System;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tesserafin.Common;
using Tesserafin.Controller;

namespace Tesserafin.Server.HealthChecks;

/// <summary>
/// Writes the <c>/health</c> body (#91 / [A5]).
/// </summary>
/// <remarks>
/// <para>
/// The schema is stable and identical for every outcome, so a probe can parse one shape:
/// <code>
/// {"status":"healthy","version":"12.0.0","database":"healthy"}
/// </code>
/// </para>
/// <para>
/// Nothing else is ever emitted. In particular the underlying <see cref="HealthReport"/>'s
/// <c>Description</c>, <c>Exception</c>, <c>Data</c> and durations are NOT serialised: this
/// endpoint is unauthenticated, and those fields are the usual route by which database paths,
/// connection strings and stack traces leak to an anonymous caller.
/// </para>
/// </remarks>
public static class HealthResponseWriter
{
    /// <summary>The overall status when every check passed.</summary>
    public const string StatusHealthy = "healthy";

    /// <summary>The overall status while the server is still coming up and has not yet taken over the pipeline.</summary>
    public const string StatusStarting = "starting";

    /// <summary>The overall status when at least one check failed.</summary>
    public const string StatusUnhealthy = "unhealthy";

    /// <summary>The database status when the database has not been probed yet (server still starting).</summary>
    public const string DatabaseUnknown = "unknown";

    /// <summary>
    /// Gets the application version reported when no <see cref="IApplicationHost"/> is available
    /// (the startup server runs before one exists). Same assembly version the running server
    /// reports, formatted the same way.
    /// </summary>
    public static string FallbackVersion { get; } =
        (typeof(HealthResponseWriter).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    /// <summary>
    /// Maps a <see cref="HealthStatus"/> to the HTTP status code the endpoint answers with.
    /// </summary>
    /// <param name="status">The aggregate health status.</param>
    /// <returns>200 only when healthy; 503 otherwise.</returns>
    /// <remarks>
    /// <see cref="HealthStatus.Degraded"/> means "the startup server is still answering", i.e. the
    /// real pipeline and its database are not up yet. ASP.NET Core's default mapping would answer
    /// 200 for that, which is exactly the false-ready signal a container probe must not receive,
    /// so it is remapped to 503 here.
    /// </remarks>
    public static int ToStatusCode(HealthStatus status)
        => status == HealthStatus.Healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;

    /// <summary>
    /// Writes the health report as the stable JSON contract.
    /// </summary>
    /// <param name="httpContext">The request context.</param>
    /// <param name="report">The aggregated health report.</param>
    /// <returns>A task representing the write.</returns>
    public static async Task WriteAsync(HttpContext httpContext, HealthReport report)
    {
        var version = httpContext.RequestServices?.GetService<IApplicationHost>()?.ApplicationVersionString
                      ?? FallbackVersion;

        var database = DatabaseUnknown;
        if (report.Entries.TryGetValue(DatabaseHealthCheck.Name, out var databaseEntry))
        {
            database = databaseEntry.Status == HealthStatus.Healthy ? StatusHealthy : StatusUnhealthy;
        }

        // The real pipeline can be serving requests before the host has finished its core
        // startup. Answering 200 in that window would be a false ready signal even when the
        // database happens to answer, so the report is held at Degraded until the host says
        // startup is done. Absent (the startup server has no IServerApplicationHost), the
        // report stands on its own.
        var effectiveStatus = report.Status;
        var applicationHost = httpContext.RequestServices?.GetService<IServerApplicationHost>();
        if (applicationHost is not null && !applicationHost.CoreStartupHasCompleted && effectiveStatus == HealthStatus.Healthy)
        {
            effectiveStatus = HealthStatus.Degraded;
        }

        var status = effectiveStatus switch
        {
            HealthStatus.Healthy => StatusHealthy,
            HealthStatus.Degraded => StatusStarting,
            _ => StatusUnhealthy
        };

        httpContext.Response.StatusCode = ToStatusCode(effectiveStatus);
        httpContext.Response.ContentType = MediaTypeNames.Application.Json;
        // A cached health answer is a wrong health answer.
        httpContext.Response.Headers.CacheControl = "no-store";

        var writer = new Utf8JsonWriter(httpContext.Response.BodyWriter);
        await using (writer.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            writer.WriteString("version", version);
            writer.WriteString(DatabaseHealthCheck.Name, database);
            writer.WriteEndObject();
            await writer.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
        }

        // Utf8JsonWriter.FlushAsync only advances the PipeWriter; the pipe itself still has to be
        // flushed. Kestrel happens to do that when it completes the response, so omitting this
        // works over real HTTP and produces an empty body anywhere else.
        await httpContext.Response.BodyWriter.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
    }
}
