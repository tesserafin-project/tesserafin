using System;
using Microsoft.AspNetCore.Http;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Api.Auth.PlaybackCapabilityPolicy;

/// <summary>
/// The capability a request presented, AFTER it was validated against the route's own demand
/// (#153-LTV-S1, made a typed request feature by #153-LTV-R1).
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AT ALL. A response that has to carry a capability onward — the Live TV HLS
/// playlist, whose segment uris the client cannot credential itself — must carry the one this
/// request was authorized with, and nothing else. Reading
/// <c>Request.Query["playbackCapability"]</c> again at the point of serving would look identical
/// and be wrong: it re-reads an attacker-controlled string with no proof that anything ever
/// accepted it, and on a route where the capability was optional it would propagate a value that
/// was never checked at all.
///
/// WHY IT IS A FEATURE AND NOT AN <c>HttpContext.Items</c> ENTRY (#153-LTV-R1). LTV-R0 found that
/// the <c>Items</c> arrangement was defended by nothing: its control M1 fed the propagator a fresh
/// query read instead of this record and the whole suite, plus the inventory gate, stayed green.
/// <c>Items</c> is an untyped <c>IDictionary&lt;object, object?&gt;</c>, so "the propagated value
/// came from a validated result" was a property of one line of a controller rather than of a type.
/// A feature is typed, is retrieved by its type rather than by a key that can be guessed or
/// shadowed, is scoped to one <see cref="HttpContext"/>, and dies with it — there is nowhere for it
/// to persist to and no way for one request to read another's.
///
/// It is written in exactly one place: the <c>IsValid</c> branch of
/// <see cref="Attributes.RequiresPlaybackCapabilityAttribute"/>. No validation, no feature, no
/// propagation.
/// </remarks>
/// <param name="Value">The presented capability value. Never logged, never written to disk.</param>
/// <param name="CapabilityId">The capability's identifier, which is safe to log.</param>
/// <param name="ItemId">The item the route named, which validation proved the capability agrees with.</param>
/// <param name="MediaSourceId">The media source the route named, which validation proved the capability agrees with.</param>
/// <param name="PlaySessionId">The play session the capability belongs to, taken from the validation result rather than from the url.</param>
/// <param name="Scope">The scope the route demanded and the capability satisfied.</param>
/// <param name="Validation">The validation result itself, carried whole so a consumer can check it rather than trust the record's existence.</param>
public sealed record ValidatedPlaybackCapability(
    string Value,
    Guid CapabilityId,
    Guid? ItemId,
    string? MediaSourceId,
    string? PlaySessionId,
    PlaybackCapabilityScope Scope,
    PlaybackCapabilityValidation Validation)
{
    /// <summary>
    /// Returns the validated capability for this request, or null if nothing validated one.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The validated capability, or null.</returns>
    public static ValidatedPlaybackCapability? From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<ValidatedPlaybackCapability>();
    }
}
