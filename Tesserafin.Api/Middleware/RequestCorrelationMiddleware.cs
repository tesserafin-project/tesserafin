using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Tesserafin.Api.Middleware;

/// <summary>
/// Issue #42: assigns exactly one correlation identifier to every HTTP request, publishes it into
/// the structured log scope for the whole request, and echoes it back on the response.
/// </summary>
/// <remarks>
/// <para>
/// Registered as early as practical in the pipeline (see <c>Startup.Configure</c>) so that every
/// log line produced while serving the request — controllers, <c>PlaybackSessionManager</c>,
/// <c>TranscodeManager</c>, and anything else reached synchronously from the request — carries the
/// identifier without any of those layers having to be threaded a parameter.
/// </para>
/// <para>
/// Deliberately NOT a playback-attempt identifier. This value changes on every request by
/// construction; the identifier that stays stable across the several requests (and retries) of one
/// playback attempt is <c>PlaybackAttemptId</c>, issue #43.
/// </para>
/// </remarks>
public class RequestCorrelationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestCorrelationMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestCorrelationMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next request delegate.</param>
    /// <param name="logger">The logger whose scope carries the identifier for the request.</param>
    public RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    /// <param name="context">Request context.</param>
    /// <returns>A task representing the rest of the pipeline.</returns>
    public async Task Invoke(HttpContext context)
    {
        var requestId = RequestCorrelation.Derive(context);
        context.Items[RequestCorrelation.ItemKey] = requestId;

        // Set on OnStarting rather than inline: the header must survive whatever the rest of the
        // pipeline does to the response, and must be present even on error responses produced by
        // ExceptionMiddleware downstream.
        context.Response.OnStarting(EchoRequestIdHeader, context);

        using (_logger.BeginScope(new Dictionary<string, object>(1)
        {
            [RequestCorrelation.LogPropertyName] = requestId
        }))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    private static Task EchoRequestIdHeader(object state)
    {
        var context = (HttpContext)state;
        var requestId = RequestCorrelation.Get(context);
        if (!string.IsNullOrEmpty(requestId))
        {
            context.Response.Headers[RequestCorrelation.ResponseHeaderName] = requestId;
        }

        return Task.CompletedTask;
    }
}
