#nullable disable

using Reefin.Controller.Net.WebSocketMessages;

namespace Reefin.Controller.Net
{
    /// <summary>
    /// Class WebSocketMessageInfo.
    /// </summary>
    public class WebSocketMessageInfo : InboundWebSocketMessage<string>
    {
        /// <summary>
        /// Gets or sets the connection.
        /// </summary>
        /// <value>The connection.</value>
        public IWebSocketConnection Connection { get; set; }
    }
}
