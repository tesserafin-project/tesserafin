using System.Collections.Generic;
using System.Net.Mime;
using Reefin.Api.Results;
using Reefin.Extensions.Json;
using Microsoft.AspNetCore.Mvc;

namespace Reefin.Api;

/// <summary>
/// Base api controller for the API setting a default route.
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces(
    MediaTypeNames.Application.Json,
    JsonDefaults.CamelCaseMediaType,
    JsonDefaults.PascalCaseMediaType)]
public class BaseReefinApiController : ControllerBase
{
    /// <summary>
    /// Create a new <see cref="OkResult{T}"/>.
    /// </summary>
    /// <param name="value">The value to return.</param>
    /// <typeparam name="T">The type to return.</typeparam>
    /// <returns>The <see cref="ActionResult{T}"/>.</returns>
    protected ActionResult<IEnumerable<T>> Ok<T>(IEnumerable<T>? value)
        => new OkResult<IEnumerable<T>?>(value);

    /// <summary>
    /// Create a new <see cref="OkResult{T}"/>.
    /// </summary>
    /// <param name="value">The value to return.</param>
    /// <typeparam name="T">The type to return.</typeparam>
    /// <returns>The <see cref="ActionResult{T}"/>.</returns>
    protected ActionResult<T> Ok<T>(T value)
        => new OkResult<T>(value);
}
