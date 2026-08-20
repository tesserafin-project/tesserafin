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

        var validated = ValidatedPlaybackCapability.From(context);
        if (validated is not null)
        {
            // Defence in depth. The feature is written on the accepted branch only, so an invalid
            // one should be unreachable — and if it ever is reachable, the answer is a refusal
            // rather than propagation of something that failed.
            return validated.Validation.IsValid
                ? PlaybackCapabilityProvenanceDecision.Propagate(validated)
                : PlaybackCapabilityProvenanceDecision.Refuse;
        }

        // A capability was presented and NOTHING validated it. Serving the request would mean
        // copying an unchecked, caller-controlled string into a response body, or answering from a
        // route whose demand was never applied. LTV-R0's control M1 turned on exactly this gap
        // being invisible: with the presentation silently ignored, "read the query" and "read the
        // validated record" produced identical responses and no test could separate them.
        var presented = context.Request.Query[PlaybackCapabilityAuthenticationHandler.QueryKey].ToString();
        return string.IsNullOrEmpty(presented)
            ? PlaybackCapabilityProvenanceDecision.NothingToPropagate
            : PlaybackCapabilityProvenanceDecision.Refuse;
    }
}
