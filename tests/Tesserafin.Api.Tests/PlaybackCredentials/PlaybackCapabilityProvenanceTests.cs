using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Xunit;

namespace Tesserafin.Api.Tests.PlaybackCredentials;

/// <summary>
/// Where a propagated capability is allowed to come from (#153-LTV-R1, LTV-R0 finding 3).
/// </summary>
/// <remarks>
/// LTV-R0's control M1 fed the HLS propagator a fresh, unvalidated
/// <c>Request.Query["playbackCapability"]</c> read instead of the validated record — the exact
/// thing <see cref="ValidatedPlaybackCapability"/>'s own remarks say must never happen — and
/// measured the result: <c>ci/run.sh</c> green, 22 assemblies, zero failures, and the inventory
/// gate 11 of 11. Nothing in the repository caught it. R0 classified that as a <i>défaut de
/// test</i> and it is what these tests close.
///
/// The reason it could not be caught was that on this route the attribute already refuses every
/// invalid presentation, so the query and the validated record always hold the same string and no
/// assertion can separate them. The separation these tests introduce is the refusal below: a
/// presented capability with no validated provenance is refused rather than quietly ignored, which
/// makes "reads the query" and "reads the record" produce different responses.
/// </remarks>
public sealed class PlaybackCapabilityProvenanceTests
{
    private const string Validated = "validated-capability-value";
    private const string Hostile = "hostile-capability-value";

    /// <summary>
    /// A capability presented in the query that nothing validated must not be propagated, and must
    /// not be silently ignored either.
    /// </summary>
    [Fact]
    public void AQueryCapabilityWithNoValidatedProvenance_IsRefused()
    {
        var context = Request(query: Hostile, validated: null);

        var decision = PlaybackCapabilityProvenance.Resolve(context);

        Assert.Equal(PlaybackCapabilityProvenanceOutcome.Refuse, decision.Outcome);
        Assert.Null(decision.Capability);
    }

    /// <summary>
    /// The validated record wins over a diverging query. Without this the two sources look alike.
    /// </summary>
    [Fact]
    public void AHostileQuery_NeverBecomesTheSource()
    {
        var context = Request(query: Hostile, validated: Capability(Validated));

        var decision = PlaybackCapabilityProvenance.Resolve(context);

        Assert.Equal(PlaybackCapabilityProvenanceOutcome.Propagate, decision.Outcome);
        Assert.Equal(Validated, decision.Capability!.Value);
    }

    /// <summary>
    /// The control that keeps the refusal from being a blanket one: a durable-token request carries
    /// no capability at all and is not refused.
    /// </summary>
    [Fact]
    public void ARequestWithNoCapabilityAnywhere_IsNotRefused()
    {
        var context = Request(query: null, validated: null);

        var decision = PlaybackCapabilityProvenance.Resolve(context);

        Assert.Equal(PlaybackCapabilityProvenanceOutcome.NothingToPropagate, decision.Outcome);
        Assert.Null(decision.Capability);
    }

    private static ValidatedPlaybackCapability Capability(string value)
        => new(value, System.Guid.NewGuid(), null, null, null);

    private static HttpContext Request(string? query, ValidatedPlaybackCapability? validated)
    {
        var context = new DefaultHttpContext();
        if (query is not null)
        {
            context.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
            {
                ["playbackCapability"] = query
            });
        }

        if (validated is not null)
        {
            context.Items[ValidatedPlaybackCapability.ItemsKey] = validated;
        }

        return context;
    }
}
