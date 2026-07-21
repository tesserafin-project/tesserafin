using System.ComponentModel;
using Tesserafin.Model.Session;

namespace Tesserafin.Controller.Net.WebSocketMessages.Outbound;

/// <summary>
/// Restart required.
/// </summary>
public class RestartRequiredMessage : OutboundWebSocketMessage
{
    /// <inheritdoc />
    [DefaultValue(SessionMessageType.RestartRequired)]
    public override SessionMessageType MessageType => SessionMessageType.RestartRequired;
}
