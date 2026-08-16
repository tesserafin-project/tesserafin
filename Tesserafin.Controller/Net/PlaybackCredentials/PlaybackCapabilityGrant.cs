using System;
using System.Collections.Generic;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// A freshly minted capability. <see cref="Value"/> is the only time the secret exists outside the
/// caller's hands — the store keeps a SHA-256 verifier and can never reproduce it.
/// </summary>
/// <param name="CapabilityId">The public identifier. Safe to log; renewal is addressed by it.</param>
/// <param name="Value">The secret, readable exactly once.</param>
/// <param name="IssuedAt">When it was minted.</param>
/// <param name="ExpiresAt">When it stops being accepted.</param>
/// <param name="Scopes">The kinds of media it may fetch.</param>
/// <param name="ItemId">The bound item, if the scopes are item-bound.</param>
/// <param name="MediaSourceId">The bound media source, if the scopes are item-bound.</param>
/// <param name="PlaySessionId">The bound play session.</param>
public readonly record struct PlaybackCapabilityGrant(
    Guid CapabilityId,
    string Value,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<PlaybackCapabilityScope> Scopes,
    Guid? ItemId,
    string? MediaSourceId,
    string PlaySessionId);
