using System.ComponentModel;
using Tesserafin.Model.Session;

namespace Tesserafin.Controller.Net.WebSocketMessages.Outbound;

/// <summary>
/// Keep alive websocket messages.
/// </summary>
public class OutboundKeepAliveMessage : OutboundWebSocketMessage
{
    /// <inheritdoc />
    [DefaultValue(SessionMessageType.KeepAlive)]
    public override SessionMessageType MessageType => SessionMessageType.KeepAlive;
}
