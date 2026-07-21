using Microsoft.AspNetCore.Authorization;

namespace Tesserafin.Api.Auth.LocalAccessOrRequiresElevationPolicy
{
    /// <summary>
    /// The local access or elevated privileges authorization requirement.
    /// </summary>
    public class LocalAccessOrRequiresElevationRequirement : IAuthorizationRequirement
    {
    }
}
