using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.Api.Auth.HlsJobOwnership;

/// <summary>
/// What the ownership authorizer decided about one request, and the binding it decided against
/// (#153-LTV-R3).
/// </summary>
/// <remarks>
/// THE BINDING TRAVELS WITH THE DECISION ON PURPOSE. A route that asked the authorizer and then
/// resolved the binding again would be reading server state twice, and a job that ended between
/// the two reads would leave the route serving a file it was authorized against a different job
/// for. There is one resolution per request, and the route may only build paths from
/// <see cref="Binding"/>.
/// </remarks>
/// <param name="Outcome">The decision.</param>
/// <param name="Binding">The job the decision was made against, on <see cref="HlsJobOwnershipOutcome.Authorized"/> only.</param>
public readonly record struct HlsJobOwnershipDecision(
    HlsJobOwnershipOutcome Outcome,
    HlsSegmentBinding? Binding)
{
    /// <summary>
    /// Gets a value indicating whether the caller may be served.
    /// </summary>
    public bool IsAuthorized => Outcome == HlsJobOwnershipOutcome.Authorized && Binding is not null;

    /// <summary>
    /// No active job owns what the caller named.
    /// </summary>
    /// <returns>The decision.</returns>
    public static HlsJobOwnershipDecision NoSuchJob() => new(HlsJobOwnershipOutcome.NoSuchJob, null);

    /// <summary>
    /// A job exists and this caller is not entitled to it.
    /// </summary>
    /// <returns>The decision.</returns>
    public static HlsJobOwnershipDecision Refused() => new(HlsJobOwnershipOutcome.Refused, null);

    /// <summary>
    /// The caller owns the job, or holds a capability that matches it exactly.
    /// </summary>
    /// <param name="binding">The job.</param>
    /// <returns>The decision.</returns>
    public static HlsJobOwnershipDecision Authorized(HlsSegmentBinding binding)
        => new(HlsJobOwnershipOutcome.Authorized, binding);
}
