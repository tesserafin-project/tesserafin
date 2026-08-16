using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// A freshly minted WebSocket ticket. As with a capability, <see cref="Value"/> exists exactly once.
/// </summary>
/// <param name="TicketId">The public identifier. Safe to log.</param>
/// <param name="Value">The secret, readable exactly once.</param>
/// <param name="IssuedAt">When it was minted.</param>
/// <param name="ExpiresAt">When it stops being accepted. Seconds, not minutes.</param>
public readonly record struct WebSocketTicketGrant(
    Guid TicketId,
    string Value,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
