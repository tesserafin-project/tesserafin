using System;

namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// The binding a WebSocket ticket is issued against.
/// </summary>
/// <param name="UserId">The authenticated user.</param>
/// <param name="SessionId">The authenticated session.</param>
/// <param name="DeviceId">The device the session belongs to.</param>
public readonly record struct WebSocketTicketRequest(Guid UserId, string SessionId, string DeviceId);
