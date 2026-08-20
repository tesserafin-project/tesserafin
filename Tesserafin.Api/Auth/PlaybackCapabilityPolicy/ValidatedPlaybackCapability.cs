using System;

namespace Tesserafin.Api.Auth.PlaybackCapabilityPolicy;

/// <summary>
/// The capability a request presented, AFTER it was validated against the route's own demand
/// (#153-LTV-S1).
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
/// So this record is written in exactly one place — the <c>IsValid</c> branch of
/// <see cref="Attributes.RequiresPlaybackCapabilityAttribute"/> — and its presence in
/// <c>HttpContext.Items</c> IS the proof. No validation, no record, no propagation.
///
/// WHY IT CARRIES THE MEDIA SOURCE. Validation compares the route's media-source demand with the
/// capability's binding and refuses unless they agree exactly, so once the record exists the two
/// are the same value. Carrying it lets the playlist name the media source in the segment uris it
/// emits, which is what makes those uris satisfy the same capability.
/// </remarks>
/// <param name="Value">The presented capability value. Never logged, never written to disk.</param>
/// <param name="CapabilityId">The capability's identifier, which is safe to log.</param>
/// <param name="ItemId">The item the route named, which validation proved the capability agrees with.</param>
/// <param name="MediaSourceId">The media source the route named, which validation proved the capability agrees with.</param>
/// <param name="PlaySessionId">The play session the capability belongs to.</param>
public sealed record ValidatedPlaybackCapability(
    string Value,
    Guid CapabilityId,
    Guid? ItemId,
    string? MediaSourceId,
    string? PlaySessionId)
{
    /// <summary>
    /// The <c>HttpContext.Items</c> key. A type-qualified constant rather than a bare string, so
    /// nothing outside this assembly can plant a look-alike entry by guessing the name.
    /// </summary>
    public static readonly object ItemsKey = new();

    /// <summary>
    /// Returns the validated capability for this request, or null if nothing validated one.
    /// </summary>
    /// <param name="items">The request's <c>HttpContext.Items</c>.</param>
    /// <returns>The validated capability, or null.</returns>
    public static ValidatedPlaybackCapability? From(System.Collections.Generic.IDictionary<object, object?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.TryGetValue(ItemsKey, out var stored) ? stored as ValidatedPlaybackCapability : null;
    }
}
