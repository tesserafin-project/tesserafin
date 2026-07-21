#nullable disable

using System.Threading;
using Tesserafin.Controller.Session;
using Tesserafin.Model.SyncPlay;

namespace Tesserafin.Controller.SyncPlay.PlaybackRequests
{
    /// <summary>
    /// Class PauseGroupRequest.
    /// </summary>
    public class PauseGroupRequest : AbstractPlaybackRequest
    {
        /// <inheritdoc />
        public override PlaybackRequestType Action { get; } = PlaybackRequestType.Pause;

        /// <inheritdoc />
        public override void Apply(IGroupStateContext context, IGroupState state, SessionInfo session, CancellationToken cancellationToken)
        {
            state.HandleRequest(this, context, state.Type, session, cancellationToken);
        }
    }
}
