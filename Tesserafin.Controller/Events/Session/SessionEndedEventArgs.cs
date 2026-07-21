using Tesserafin.Controller.Session;
using Tesserafin.Data.Events;

namespace Tesserafin.Controller.Events.Session
{
    /// <summary>
    /// An event that fires when a session is ended.
    /// </summary>
    public class SessionEndedEventArgs : GenericEventArgs<SessionInfo>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SessionEndedEventArgs"/> class.
        /// </summary>
        /// <param name="arg">The session info.</param>
        public SessionEndedEventArgs(SessionInfo arg) : base(arg)
        {
        }
    }
}
