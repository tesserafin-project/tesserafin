using System.ComponentModel;
using Reefin.Model.Session;

namespace Reefin.Controller.Net.WebSocketMessages.Outbound;

/// <summary>
/// Keep alive websocket messages.
/// </summary>
public class OutboundKeepAliveMessage : OutboundWebSocketMessage
{
    /// <inheritdoc />
    [DefaultValue(SessionMessageType.KeepAlive)]
    public override SessionMessageType MessageType => SessionMessageType.KeepAlive;
}
