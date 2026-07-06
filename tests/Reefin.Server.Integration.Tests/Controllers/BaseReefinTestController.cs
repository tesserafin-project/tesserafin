using Reefin.Api;
using Microsoft.AspNetCore.Mvc;

namespace Reefin.Server.Integration.Tests.Controllers
{
    /// <summary>
    /// Base controller for testing infrastructure.
    /// Automatically ignored in generated openapi spec.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    public class BaseReefinTestController : BaseReefinApiController
    {
    }
}
