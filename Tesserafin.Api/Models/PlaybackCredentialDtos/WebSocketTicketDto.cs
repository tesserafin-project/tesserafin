using System;

namespace Tesserafin.Api.Models.PlaybackCredentialDtos;

/// <summary>
/// A minted single-use WebSocket ticket.
/// </summary>
/// <remarks>
/// Valid only during a WebSocket upgrade and only once. It authenticates no HTTP request — media or
/// general — because the HTTP authorization path never reads the query key it travels in.
/// </remarks>
public class WebSocketTicketDto
{
    /// <summary>
    /// Gets or sets the public identifier. Safe to log.
    /// </summary>
    public Guid TicketId { get; set; }

    /// <summary>
    /// Gets or sets the secret to present on the upgrade request.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the ticket was issued.
    /// </summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// Gets or sets when the ticket stops being accepted. Seconds after issue, not minutes.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
