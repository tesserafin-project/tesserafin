using System;
using Microsoft.AspNetCore.Http;

namespace Tesserafin.Api.Auth.PlaybackCapabilityPolicy;

/// <summary>
/// Decides, for one request, whether a capability may be carried into a response body — and where
/// that capability is allowed to come from (#153-LTV-R1).
/// </summary>
/// <remarks>
/// WHY THIS IS A SEAM AT ALL. LTV-R0's control M1 replaced <c>validated.Value</c> in
/// <c>DynamicHlsController</c> with a fresh <c>Request.Query["playbackCapability"]</c> read and
/// found that <b>nothing in the repository turned red</b>: the whole suite and the inventory gate
/// stayed green. The reason it stayed green is that the two variants are behaviourally identical
/// on that route today, so no assertion could separate them. Concentrating the decision here makes
/// it separable: the resolver is the only thing that names a source, and it can be tested directly.
/// </remarks>
public static class PlaybackCapabilityProvenance
{
    /// <summary>
    /// Resolves what this request is allowed to propagate.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The decision.</returns>
    public static PlaybackCapabilityProvenanceDecision Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var validated = ValidatedPlaybackCapability.From(context.Items);
        return validated is null
            ? PlaybackCapabilityProvenanceDecision.NothingToPropagate
            : PlaybackCapabilityProvenanceDecision.Propagate(validated);
    }
}
