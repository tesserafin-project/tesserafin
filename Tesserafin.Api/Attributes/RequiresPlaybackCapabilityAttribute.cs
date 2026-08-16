using System;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Constants;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Api.Attributes;

/// <summary>
/// Narrows a media route to one playback-capability scope, and to the item and media source the
/// route itself names (#153).
/// </summary>
/// <remarks>
/// WHY THIS IS SEPARATE FROM AUTHENTICATION. Authentication proves the presented value is a live
/// capability belonging to a live session. Only the route knows which scope it is, which item it is
/// for and which media source. Deciding both in one place would mean the authentication layer
/// guessing a demand, and a guess that is too wide accepts a subtitle capability for a video
/// stream while a guess that is too narrow rejects a fallback font outright.
///
/// WHAT IT DOES TO A DURABLE-TOKEN REQUEST. Nothing. A0 does not remove the legacy query-token
/// path, and this filter only constrains a principal that a capability authenticated. Constraining
/// the durable token here would change legacy client behaviour in a stage whose whole point is that
/// it does not.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequiresPlaybackCapabilityAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly PlaybackCapabilityScope _scope;
    private readonly string? _itemRouteKey;
    private readonly string? _mediaSourceRouteKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequiresPlaybackCapabilityAttribute"/> class.
    /// </summary>
    /// <param name="scope">The single scope this route demands.</param>
    /// <param name="itemRouteKey">The route or query key naming the item, if the route names one.</param>
    /// <param name="mediaSourceRouteKey">The route or query key naming the media source, if the route names one.</param>
    public RequiresPlaybackCapabilityAttribute(
        PlaybackCapabilityScope scope,
        string? itemRouteKey = null,
        string? mediaSourceRouteKey = null)
    {
        _scope = scope;
        _itemRouteKey = itemRouteKey;
        _mediaSourceRouteKey = mediaSourceRouteKey;
    }

    /// <summary>
    /// Gets the single scope this route demands. Public so the boundary can be asserted route by
    /// route without booting a server: "a capability reaches media and nothing else" is a property
    /// of the route table, and a request-level test can only sample it.
    /// </summary>
    public PlaybackCapabilityScope Scope => _scope;

    /// <inheritdoc />
    public System.Threading.Tasks.Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var presented = context.HttpContext.Request.Query[PlaybackCapabilityAuthenticationHandler.QueryKey].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            // No capability was presented, so there is nothing to narrow. Whatever authenticated
            // this request already satisfied the policy.
            return System.Threading.Tasks.Task.CompletedTask;
        }

        var credentialService = context.HttpContext.RequestServices.GetRequiredService<IPlaybackCredentialService>();
        var validation = credentialService.ValidateCapability(
            presented,
            new PlaybackCapabilityDemand(_scope, ReadGuid(context, _itemRouteKey), ReadString(context, _mediaSourceRouteKey)));

        if (!validation.IsValid)
        {
            // One undifferentiated refusal. Scope, item, media source and session mismatches all
            // answer the same way, so a caller cannot use the response to map what a capability is
            // bound to.
            context.Result = new UnauthorizedResult();
        }

        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static string? ReadString(AuthorizationFilterContext context, string? key)
    {
        if (key is null)
        {
            return null;
        }

        if (context.RouteData.Values.TryGetValue(key, out var routeValue) && routeValue is not null)
        {
            return Convert.ToString(routeValue, CultureInfo.InvariantCulture);
        }

        var queryValue = context.HttpContext.Request.Query[key].ToString();
        return string.IsNullOrEmpty(queryValue) ? null : queryValue;
    }

    private static Guid? ReadGuid(AuthorizationFilterContext context, string? key)
        => Guid.TryParse(ReadString(context, key), out var parsed) ? parsed : null;
}
