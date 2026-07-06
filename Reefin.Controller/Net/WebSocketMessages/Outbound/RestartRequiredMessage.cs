using System.ComponentModel;
using Reefin.Model.Session;

namespace Reefin.Controller.Net.WebSocketMessages.Outbound;

/// <summary>
/// Restart required.
/// </summary>
public class RestartRequiredMessage : OutboundWebSocketMessage
{
    /// <inheritdoc />
    [DefaultValue(SessionMessageType.RestartRequired)]
    public override SessionMessageType MessageType => SessionMessageType.RestartRequired;
}
