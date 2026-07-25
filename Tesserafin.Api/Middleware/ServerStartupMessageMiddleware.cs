using System;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Tesserafin.Controller;
using Tesserafin.Model.Globalization;

namespace Tesserafin.Api.Middleware;

/// <summary>
/// Shows a custom message during server startup.
/// </summary>
public class ServerStartupMessageMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerStartupMessageMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    public ServerStartupMessageMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Executes the middleware action.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="serverApplicationHost">The server application host.</param>
    /// <param name="localizationManager">The localization manager.</param>
    /// <returns>The async task.</returns>
    public async Task Invoke(
        HttpContext httpContext,
        IServerApplicationHost serverApplicationHost,
        ILocalizationManager localizationManager)
    {
        // `/health` is exempt for the same reason `/system/ping` is, and it matters more:
        // it is the endpoint container runtimes and reverse proxies poll, and its whole
        // value is a single stable JSON shape (#91 / [A5]). Swallowing it here would
        // answer a plain-text HTML body during startup instead, so a probe would have to
        // parse two formats — and the one it is most likely to hit first is the wrong one.
        // Letting it through is safe: the endpoint reports `status":"starting"` with a 503
        // until core startup completes, which is the same "not ready" answer, in contract.
        if (serverApplicationHost.CoreStartupHasCompleted
            || httpContext.Request.Path.Equals("/system/ping", StringComparison.OrdinalIgnoreCase)
            || httpContext.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(httpContext).ConfigureAwait(false);
            return;
        }

        var message = localizationManager.GetLocalizedString("StartupEmbyServerIsLoading");
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.ContentType = MediaTypeNames.Text.Html;
        await httpContext.Response.WriteAsync(message, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
