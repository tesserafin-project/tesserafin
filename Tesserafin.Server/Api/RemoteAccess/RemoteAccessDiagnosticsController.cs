using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api;
using Tesserafin.Common.Api;
using Tesserafin.Server.Api.RemoteAccess.Models;
using Tesserafin.Server.Diagnostics.RemoteAccess;

namespace Tesserafin.Server.Api.RemoteAccess;

/// <summary>
/// Reports what the server can observe about its own remote-access posture.
/// </summary>
/// <remarks>
/// WHY THIS CONTROLLER LIVES IN <c>Tesserafin.Server</c>. The reference direction is
/// <c>Tesserafin.Server → Tesserafin.Server.Core → Tesserafin.Api</c>, and the R1-A engine lives
/// in <c>Tesserafin.Server</c>. A controller in <c>Tesserafin.Api</c> would have to reference back
/// into <c>Tesserafin.Server</c> and close a cycle. The engine is not moved and not duplicated to
/// avoid that; instead this assembly is registered as an MVC application part, which is the same
/// mechanism plugins already use.
///
/// WHAT THIS ENDPOINT IS NOT. It observes and reports. It writes no configuration, opens no
/// connection beyond the single bounded hostname lookup the engine already performs, starts
/// nothing in the background, touches no R0-B publication evidence, and reaches no verdict. It
/// cannot tell an operator that remote access is ready, secure or working, because nothing
/// running inside the host can know that — and the four permanent-uncertainty findings in every
/// report say so out loud rather than leaving a convenient silence.
/// </remarks>
[Route("System/RemoteAccess")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class RemoteAccessDiagnosticsController : BaseTesserafinApiController
{
    private readonly RemoteAccessDiagnosticCollector _collector;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteAccessDiagnosticsController"/> class.
    /// </summary>
    /// <param name="collector">The shared diagnostic collector.</param>
    /// <remarks>
    /// The collector is a singleton and is injected rather than constructed. Its
    /// one-at-a-time invariant is a field on the instance, so a per-request collector would give
    /// every request its own semaphore and serialise nothing at all.
    /// </remarks>
    public RemoteAccessDiagnosticsController(RemoteAccessDiagnosticCollector collector)
    {
        _collector = collector;
    }

    /// <summary>
    /// Collects a read-only remote-access diagnostic report.
    /// </summary>
    /// <param name="request">The publication intent to diagnose against.</param>
    /// <param name="cancellationToken">Propagated into collection and hostname resolution.</param>
    /// <response code="200">The report was collected.</response>
    /// <response code="400">The request body was malformed or a family policy was missing or unrecognised.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not an elevated administrator.</response>
    /// <returns>The diagnostic report.</returns>
    /// <remarks>
    /// POST, with the hostname in the body — never in the route and never in the query string. A
    /// hostname in a URL is copied into access logs, proxy logs and browser history by parties who
    /// never agreed to hold it, and there is no way to un-log it afterwards.
    ///
    /// An unusable hostname is answered, not rejected. Absent produces
    /// <c>HostnameNotProvided</c>; syntactically invalid produces
    /// <c>HostnameSyntacticallyInvalid</c> and never reaches the resolver. Both are HTTP 200,
    /// because "what you typed cannot be a hostname" is the diagnostic an operator came here for,
    /// not a transport failure to be handed back as a 400.
    /// </remarks>
    [HttpPost("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RemoteAccessDiagnosticsReportDto>> CollectRemoteAccessDiagnostics(
        [FromBody, Required] RemoteAccessDiagnosticsRequestDto request,
        CancellationToken cancellationToken)
    {
        // A report describes one instant on one host. Caching it — anywhere, for any duration —
        // would let an operator act on a posture that has already changed, which is worse than
        // having no report at all.
        Response.Headers.CacheControl = "no-store";

        var report = await _collector
            .CollectAsync(RemoteAccessDiagnosticsProjector.ToInput(request), cancellationToken)
            .ConfigureAwait(false);

        // Projected, never serialised directly: the internal report is an implementation record,
        // and publishing it would make every future edit to it an unreviewed contract change.
        return RemoteAccessDiagnosticsProjector.ToWire(report);
    }
}
