using System.ComponentModel;
using Tesserafin.Model.Session;

namespace Tesserafin.Controller.Net.WebSocketMessages.Outbound;

/// <summary>
/// Server shutting down message.
/// </summary>
public class ServerShuttingDownMessage : OutboundWebSocketMessage
{
    /// <inheritdoc />
    [DefaultValue(SessionMessageType.ServerShuttingDown)]
    public override SessionMessageType MessageType => SessionMessageType.ServerShuttingDown;
}
