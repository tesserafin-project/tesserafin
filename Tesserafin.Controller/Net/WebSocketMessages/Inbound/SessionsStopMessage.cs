using System.ComponentModel;
using Tesserafin.Model.Session;

namespace Tesserafin.Controller.Net.WebSocketMessages.Inbound;

/// <summary>
/// Sessions stop message.
/// </summary>
public class SessionsStopMessage : InboundWebSocketMessage
{
    /// <inheritdoc />
    [DefaultValue(SessionMessageType.SessionsStop)]
    public override SessionMessageType MessageType => SessionMessageType.SessionsStop;
}
