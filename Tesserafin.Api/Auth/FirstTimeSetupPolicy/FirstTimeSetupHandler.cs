using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Extensions;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Net;

namespace Tesserafin.Api.Auth.FirstTimeSetupPolicy
{
    /// <summary>
    /// Authorization handler for requiring first time setup or default privileges.
    /// </summary>
    public class FirstTimeSetupHandler : AuthorizationHandler<FirstTimeSetupRequirement>
    {
        private readonly IConfigurationManager _configurationManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAuthorizationContext _authorizationContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="FirstTimeSetupHandler" /> class.
        /// </summary>
        /// <param name="configurationManager">Instance of the <see cref="IConfigurationManager"/> interface.</param>
        /// <param name="httpContextAccessor">Instance of the <see cref="IHttpContextAccessor"/> interface.</param>
        /// <param name="authorizationContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
        public FirstTimeSetupHandler(
            IConfigurationManager configurationManager,
            IHttpContextAccessor httpContextAccessor,
            IAuthorizationContext authorizationContext)
        {
            _configurationManager = configurationManager;
            _httpContextAccessor = httpContextAccessor;
            _authorizationContext = authorizationContext;
        }

        /// <inheritdoc />
        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, FirstTimeSetupRequirement requirement)
        {
            // Succeed if the startup wizard / first time setup is not complete, but only for the
            // endpoints onboarding actually needs and only for a caller that presented no token.
            if (!_configurationManager.CommonConfiguration.IsStartupWizardCompleted
                && await IsPreOnboardingGrantAllowedAsync().ConfigureAwait(false))
            {
                context.Succeed(requirement);
            }

            // Succeed if user is admin
            else if (context.User.IsInRole(UserRoles.Administrator))
            {
                context.Succeed(requirement);
            }

            // Fail if admin is required and user is not admin
            else if (requirement.RequireAdmin)
            {
                context.Fail();
            }

            // Succeed if admin is not required and user is not guest
            else if (context.User.IsInRole(UserRoles.User))
            {
                context.Succeed(requirement);
            }

            // Any user-specific checks are handled in the DefaultAuthorizationHandler.
        }

        /// <summary>
        /// Determines whether the pre-onboarding grant may be applied to the current request.
        /// </summary>
        /// <remarks>
        /// Two conditions, both required. The endpoint must be explicitly marked as part of the
        /// onboarding surface, so that an unrelated privileged endpoint cannot inherit the grant
        /// merely by carrying a policy built on <see cref="FirstTimeSetupRequirement"/>. And the
        /// request must carry no token at all: a caller that presented a token — valid, expired or
        /// malformed — is judged on that token through the role branches, never on the setup
        /// window. Onboarding itself never presents a token, so this does not restrict the wizard.
        /// </remarks>
        private async Task<bool> IsPreOnboardingGrantAllowedAsync()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is null)
            {
                // No request to judge: fail closed.
                return false;
            }

            if (httpContext.GetEndpoint()?.Metadata.GetMetadata<FirstTimeSetupEndpointAttribute>() is null)
            {
                return false;
            }

            var authorizationInfo = await _authorizationContext.GetAuthorizationInfo(httpContext).ConfigureAwait(false);
            return !authorizationInfo.HasToken;
        }
    }
}
