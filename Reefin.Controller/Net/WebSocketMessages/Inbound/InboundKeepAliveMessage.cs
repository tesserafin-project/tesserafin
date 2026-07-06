using System.ComponentModel;
using Reefin.Model.Session;

namespace Reefin.Controller.Net.WebSocketMessages.Inbound;

/// <summary>
/// Keep alive websocket messages.
/// </summary>
public class InboundKeepAliveMessage : InboundWebSocketMessage
{
    /// <inheritdoc />
    [DefaultValue(SessionMessageType.KeepAlive)]
    public override SessionMessageType MessageType => SessionMessageType.KeepAlive;
}
