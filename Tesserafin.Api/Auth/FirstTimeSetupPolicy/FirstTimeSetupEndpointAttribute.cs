using System;

namespace Tesserafin.Api.Auth.FirstTimeSetupPolicy
{
    /// <summary>
    /// Marks an endpoint as part of the first-time onboarding surface.
    /// </summary>
    /// <remarks>
    /// The pre-onboarding authorization grant exists only so that the setup wizard can run before
    /// any credential exists. It is deliberately opt-in per endpoint: carrying a policy built on
    /// <see cref="FirstTimeSetupRequirement"/> for the sake of its "or elevated" half must not also
    /// make an endpoint reachable without credentials. An endpoint that is not marked here is never
    /// granted access by the pre-onboarding branch, whatever policy it carries.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class FirstTimeSetupEndpointAttribute : Attribute
    {
    }
}
