using System.ComponentModel;
using Tesserafin.Model.Session;

namespace Tesserafin.Controller.Net.WebSocketMessages.Inbound;

/// <summary>
/// Keep alive websocket messages.
/// </summary>
public class InboundKeepAliveMessage : InboundWebSocketMessage
{
    /// <inheritdoc />
    [DefaultValue(SessionMessageType.KeepAlive)]
    public override SessionMessageType MessageType => SessionMessageType.KeepAlive;
}
