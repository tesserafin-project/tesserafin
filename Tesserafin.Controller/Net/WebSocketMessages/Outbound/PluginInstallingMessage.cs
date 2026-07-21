using System.ComponentModel;
using Tesserafin.Model.Session;
using Tesserafin.Model.Updates;

namespace Tesserafin.Controller.Net.WebSocketMessages.Outbound;

/// <summary>
/// Package installing message.
/// </summary>
public class PluginInstallingMessage : OutboundWebSocketMessage<InstallationInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginInstallingMessage"/> class.
    /// </summary>
    /// <param name="data">Installation info.</param>
    public PluginInstallingMessage(InstallationInfo data)
        : base(data)
    {
    }

    /// <inheritdoc />
    [DefaultValue(SessionMessageType.PackageInstalling)]
    public override SessionMessageType MessageType => SessionMessageType.PackageInstalling;
}
