using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Tesserafin.Api.Middleware;

/// <summary>
/// Issue #42: derivation and per-request storage of the request correlation identifier. Split out
/// of <see cref="RequestCorrelationMiddleware"/> so the derivation rule itself is directly
/// unit-testable against a bare <see cref="DefaultHttpContext"/>, without standing up a pipeline.
/// </summary>
/// <remarks>
/// <b>One identifier per HTTP request.</b> This is deliberately not stable across requests: two
/// requests on the same playback session, or the same playback attempt, get two different values.
/// The identifier stable across a whole attempt is <c>PlaybackAttemptId</c> (issue #43), which is a
/// separate, client-supplied field — see <see cref="Tesserafin.Controller.Diagnostics.IRequestCorrelationAccessor"/>
/// for the full scope hierarchy.
/// </remarks>
public static class RequestCorrelation
{
    /// <summary>
    /// The response header the correlation identifier is always echoed on, so an operator reading a
    /// client-side capture can join it to the server log for the same request.
    /// </summary>
    public const string ResponseHeaderName = "X-Request-Id";

    /// <summary>
    /// The <see cref="HttpContext.Items"/> key the derived value is stored under for the lifetime of
    /// the request.
    /// </summary>
    public const string ItemKey = "Tesserafin.RequestId";

    /// <summary>
    /// The structured-log property name the identifier is published under, both in the
    /// <c>BeginScope</c> state and in individual message templates. One name everywhere, so a log
    /// query never has to know which layer emitted the line.
    /// </summary>
    public const string LogPropertyName = "RequestId";

    /// <summary>
    /// Derives the correlation identifier for <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Prefers <see cref="Activity"/>.<see cref="Activity.TraceId"/> — the W3C trace id — over the
    /// server-local <see cref="HttpContext.TraceIdentifier"/>. That preference is what makes an
    /// inbound <c>traceparent</c> header work end to end for free: ASP.NET Core's hosting layer has
    /// already parsed it into the ambient <see cref="Activity"/> by the time any middleware runs, so
    /// reading the trace id here adopts the caller's trace instead of minting an unrelated one. When
    /// no <see cref="Activity"/> is in flight (distributed tracing switched off), the per-request
    /// <see cref="HttpContext.TraceIdentifier"/> is used — still unique per request, just not
    /// joinable to an upstream trace.
    /// </remarks>
    /// <param name="context">The request being served.</param>
    /// <returns>A non-empty identifier, unique to this request.</returns>
    public static string Derive(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var activity = Activity.Current;
        if (activity is not null && activity.IdFormat == ActivityIdFormat.W3C)
        {
            var traceId = activity.TraceId.ToHexString();
            if (!string.IsNullOrEmpty(traceId) && traceId != default(ActivityTraceId).ToHexString())
            {
                return traceId;
            }
        }

        return context.TraceIdentifier;
    }

    /// <summary>
    /// Reads back the identifier <see cref="RequestCorrelationMiddleware"/> stored for this request.
    /// </summary>
    /// <param name="context">The request being served, or <c>null</c> when there is none.</param>
    /// <returns>
    /// The stored identifier, or <c>null</c> when <paramref name="context"/> is <c>null</c> or the
    /// middleware never ran for it — never an exception. Correlation is diagnostics; its absence
    /// must never be able to fail a request.
    /// </returns>
    public static string? Get(HttpContext? context)
    {
        if (context is null)
        {
            return null;
        }

        return context.Items.TryGetValue(ItemKey, out var value) ? value as string : null;
    }
}
