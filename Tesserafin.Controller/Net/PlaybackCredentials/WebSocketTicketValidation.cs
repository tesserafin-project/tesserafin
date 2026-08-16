using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// The outcome of consuming a WebSocket ticket.
/// </summary>
/// <param name="IsValid">Whether the upgrade may proceed.</param>
/// <param name="Failure">Why not, when it may not.</param>
/// <param name="UserId">The bound user, on success.</param>
/// <param name="SessionId">The bound session, on success.</param>
/// <param name="DeviceId">The bound device, on success.</param>
public readonly record struct WebSocketTicketValidation(
    bool IsValid,
    WebSocketTicketFailure Failure,
    Guid UserId,
    string? SessionId,
    string? DeviceId);
