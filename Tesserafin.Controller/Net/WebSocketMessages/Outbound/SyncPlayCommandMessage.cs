using System.ComponentModel;
using Tesserafin.Model.Session;
using Tesserafin.Model.SyncPlay;

namespace Tesserafin.Controller.Net.WebSocketMessages.Outbound;

/// <summary>
/// Sync play command.
/// </summary>
public class SyncPlayCommandMessage : OutboundWebSocketMessage<SendCommand>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SyncPlayCommandMessage"/> class.
    /// </summary>
    /// <param name="data">The send command.</param>
    public SyncPlayCommandMessage(SendCommand data)
        : base(data)
    {
    }

    /// <inheritdoc />
    [DefaultValue(SessionMessageType.SyncPlayCommand)]
    public override SessionMessageType MessageType => SessionMessageType.SyncPlayCommand;
}
