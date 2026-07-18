using Microsoft.AspNetCore.Http;
using Reefin.Controller.Diagnostics;

namespace Reefin.Api.Middleware;

/// <summary>
/// Issue #42: the hosted implementation of <see cref="IRequestCorrelationAccessor"/> — reads back
/// the identifier <see cref="RequestCorrelationMiddleware"/> stored on the ambient
/// <see cref="HttpContext"/>.
/// </summary>
/// <remarks>
/// Returns <c>null</c> whenever there is no ambient request: a timer callback (for example
/// <c>TranscodeManager</c>'s kill timer), a scheduled task, or startup. That is the correct answer,
/// not a failure — those log lines simply carry no request id, because no request caused them.
/// </remarks>
public sealed class HttpRequestCorrelationAccessor : IRequestCorrelationAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpRequestCorrelationAccessor"/> class.
    /// </summary>
    /// <param name="httpContextAccessor">Instance of the <see cref="IHttpContextAccessor"/> interface.</param>
    public HttpRequestCorrelationAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public string? CurrentRequestId => RequestCorrelation.Get(_httpContextAccessor.HttpContext);
}
