using System;
using System.Collections.Generic;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Api.Models.PlaybackCredentialDtos;

/// <summary>
/// A minted playback capability.
/// </summary>
/// <remarks>
/// <see cref="Value"/> is returned by the minting call and by nothing else, ever. The server keeps
/// a SHA-256 verifier and cannot reproduce the value, so a client that loses it mints a new
/// capability rather than asking for this one again.
/// </remarks>
public class PlaybackCapabilityDto
{
    /// <summary>
    /// Gets or sets the public identifier. Renewal is addressed by this, not by the secret, so the
    /// secret never travels a second time. Safe to log.
    /// </summary>
    public Guid CapabilityId { get; set; }

    /// <summary>
    /// Gets or sets the secret to present on media requests. Sensitive despite being short-lived:
    /// short-lived is not non-sensitive.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the capability was issued.
    /// </summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// Gets or sets when the capability stops being accepted.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the kinds of media this capability may fetch.
    /// </summary>
    public IReadOnlyList<PlaybackCapabilityScope> Scopes { get; set; } = Array.Empty<PlaybackCapabilityScope>();

    /// <summary>
    /// Gets or sets the bound item, when the scopes are item-bound.
    /// </summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the bound media source, when the scopes are item-bound.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the bound play session.
    /// </summary>
    public string PlaySessionId { get; set; } = string.Empty;
}
