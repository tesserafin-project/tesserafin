using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Extensions;
using Tesserafin.Extensions;

namespace Tesserafin.Api.Auth.MediaDeliveryPolicy;

/// <summary>
/// Succeeds a media request that carries either a live playback capability or an ordinary
/// authenticated user (#153).
/// </summary>
/// <remarks>
/// Reaching this handler at all already means <c>DefaultAuthorizationHandler</c> did not call
/// <c>Fail</c>, because a failure there is decisive for the whole requirement. So the remote-access
/// permission and the parental schedule have already been applied to a durable-token principal by
/// the time this runs, and this handler does not re-implement them — duplicating those checks is
/// how two code paths drift into disagreeing about who may watch what.
/// </remarks>
public class MediaDeliveryHandler : AuthorizationHandler<MediaDeliveryRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MediaDeliveryRequirement requirement)
    {
        // A capability principal carries no user permissions to evaluate: the capability itself IS
        // the grant, and it was already bound to a user, a session, a play session and a media
        // scope when it was minted.
        if (context.User.HasClaim(c => string.Equals(c.Type, InternalClaimTypes.PlaybackCapabilityId, System.StringComparison.Ordinal)))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.GetIsApiKey() || !context.User.GetUserId().IsEmpty())
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
