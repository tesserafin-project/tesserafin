#nullable disable

using Tesserafin.Controller.Net.WebSocketMessages;

namespace Tesserafin.Controller.Net
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
