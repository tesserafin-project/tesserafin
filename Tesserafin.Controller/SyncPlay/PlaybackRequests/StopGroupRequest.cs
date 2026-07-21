using System.Threading;
using Tesserafin.Controller.Session;
using Tesserafin.Model.SyncPlay;

namespace Tesserafin.Controller.SyncPlay.PlaybackRequests
{
    /// <summary>
    /// Class StopGroupRequest.
    /// </summary>
    public class StopGroupRequest : AbstractPlaybackRequest
    {
        /// <inheritdoc />
        public override PlaybackRequestType Action { get; } = PlaybackRequestType.Stop;

        /// <inheritdoc />
        public override void Apply(IGroupStateContext context, IGroupState state, SessionInfo session, CancellationToken cancellationToken)
        {
            state.HandleRequest(this, context, state.Type, session, cancellationToken);
        }
    }
}
