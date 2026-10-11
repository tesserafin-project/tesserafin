using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Extensions;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions;

namespace Tesserafin.Api.Attributes;

/// <summary>
/// Refuses a request for an item its user may not see, on a route that serves a transcode's files
/// without resolving the item itself.
/// </summary>
/// <remarks>
/// The legacy HLS routes resolve a job, compare the caller with the job's owner and the route's
/// item with the job's - and serve. That a user started a transcode says it could see the item
/// then; this asks whether it still can, on every request, with the predicate every other route
/// uses. An API key names no user and is not restricted, as everywhere.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresVisibleItemAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RequiresVisibleItemAttribute"/> class.
    /// </summary>
    /// <param name="itemRouteKey">The route key naming the item.</param>
    public RequiresVisibleItemAttribute(string itemRouteKey)
    {
        ItemRouteKey = itemRouteKey;
    }

    /// <summary>
    /// Gets the route key naming the item.
    /// </summary>
    public string ItemRouteKey { get; }

    /// <inheritdoc />
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userId = context.HttpContext.User.GetUserId();
        if (userId.IsEmpty())
        {
            return;
        }

        // The same answer as for a job that does not exist.
        if (!Guid.TryParse(context.RouteData.Values[ItemRouteKey]?.ToString(), out var itemId)
            || context.HttpContext.RequestServices.GetRequiredService<ILibraryManager>().GetItemById<BaseItem>(itemId, userId) is null)
        {
            context.Result = new NotFoundObjectResult("Hls segment not found.");
        }
    }
}
