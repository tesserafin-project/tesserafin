using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Controller.Net.PlaybackCredentials;
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
/// assertion could separate them. The separation introduced here is the refusal below: a presented
/// capability with no validated provenance is refused rather than quietly ignored, which makes
/// "reads the query" and "reads the feature" produce different responses.
/// </remarks>
public sealed class PlaybackCapabilityProvenanceTests
{
    private const string Validated = "validated-capability-value";
    private const string Hostile = "hostile-capability-value";
    private const string ValidatedMediaSource = "6d5da76e3955fd1005f75c496c371521";
    private const string HostileMediaSource = "ffffffffffffffffffffffffffffffff";

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
    /// The validated feature wins over a diverging query. Without this the two sources look alike.
    /// </summary>
    [Fact]
    public void AHostileQuery_NeverBecomesTheSource()
    {
        var context = Request(query: Hostile, validated: Capability(Validated, ValidatedMediaSource));
        context.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
        {
            ["playbackCapability"] = Hostile,
            ["mediaSourceId"] = HostileMediaSource
        });

        var decision = PlaybackCapabilityProvenance.Resolve(context);

        Assert.Equal(PlaybackCapabilityProvenanceOutcome.Propagate, decision.Outcome);
        Assert.Equal(Validated, decision.Capability!.Value);
    }

    /// <summary>
    /// The bindings that get propagated come from the feature too, not only the value. A propagator
    /// fed the right secret and the caller's own media source would emit uris nothing accepts —
    /// or, worse, uris naming a media source the caller chose.
    /// </summary>
    [Fact]
    public void ThePropagatedBindings_ComeFromTheFeatureAndNotFromTheQuery()
    {
        var context = Request(query: Hostile, validated: Capability(Validated, ValidatedMediaSource));
        context.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
        {
            ["playbackCapability"] = Hostile,
            ["mediaSourceId"] = HostileMediaSource
        });

        var decision = PlaybackCapabilityProvenance.Resolve(context);

        Assert.Equal(ValidatedMediaSource, decision.Capability!.MediaSourceId);
        Assert.NotEqual(HostileMediaSource, decision.Capability.MediaSourceId);
    }

    /// <summary>
    /// A feature that carries a failed validation is refused even though a valid value sits in the
    /// query beside it. The feature is written on the accepted branch only, so this should be
    /// unreachable — and if it ever is reachable the answer is a refusal, not propagation.
    /// </summary>
    [Fact]
    public void AnInvalidFeature_IsRefusedEvenWithAValidQuery()
    {
        var context = Request(query: Validated, validated: Capability(Validated, ValidatedMediaSource, isValid: false));

        var decision = PlaybackCapabilityProvenance.Resolve(context);

        Assert.Equal(PlaybackCapabilityProvenanceOutcome.Refuse, decision.Outcome);
        Assert.Null(decision.Capability);
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

    /// <summary>
    /// One request's provenance is not another's. The feature lives on the
    /// <see cref="HttpContext"/> and is retrieved by type from that context alone, so a second
    /// request cannot see the first's however it is shaped.
    /// </summary>
    [Fact]
    public void TheFeatureOfOneRequest_IsNotVisibleToAnother()
    {
        var first = Request(query: Validated, validated: Capability(Validated, ValidatedMediaSource));
        Assert.Equal(PlaybackCapabilityProvenanceOutcome.Propagate, PlaybackCapabilityProvenance.Resolve(first).Outcome);

        var second = Request(query: Validated, validated: null);

        Assert.Null(ValidatedPlaybackCapability.From(second));
        Assert.Equal(PlaybackCapabilityProvenanceOutcome.Refuse, PlaybackCapabilityProvenance.Resolve(second).Outcome);
    }

    /// <summary>
    /// Nothing survives the request. Recycling the <see cref="HttpContext"/> the way the server
    /// itself does — a fresh feature collection on the same object — leaves no provenance behind.
    /// </summary>
    [Fact]
    public void NoProvenance_SurvivesTheRequestItWasValidatedOn()
    {
        var context = Request(query: Validated, validated: Capability(Validated, ValidatedMediaSource));
        Assert.NotNull(ValidatedPlaybackCapability.From(context));

        ((DefaultHttpContext)context).Initialize(new FeatureCollection());

        Assert.Null(ValidatedPlaybackCapability.From(context));
    }

    private static ValidatedPlaybackCapability Capability(string value, string? mediaSourceId, bool isValid = true)
        => new(
            value,
            Guid.NewGuid(),
            null,
            mediaSourceId,
            "play-session-a",
            PlaybackCapabilityScope.Media,
            new PlaybackCapabilityValidation(
                isValid,
                isValid ? PlaybackCapabilityFailure.None : PlaybackCapabilityFailure.Unknown,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "session-a",
                "play-session-a"));

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
            context.Features.Set(validated);
        }

        return context;
    }
}
