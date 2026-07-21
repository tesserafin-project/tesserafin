using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api;

namespace Tesserafin.Server.Integration.Tests.Controllers
{
    /// <summary>
    /// Base controller for testing infrastructure.
    /// Automatically ignored in generated openapi spec.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    public class BaseTesserafinTestController : BaseTesserafinApiController
    {
    }
}
