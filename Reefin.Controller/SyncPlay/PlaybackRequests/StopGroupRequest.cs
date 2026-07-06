using System.Threading;
using Reefin.Controller.Session;
using Reefin.Model.SyncPlay;

namespace Reefin.Controller.SyncPlay.PlaybackRequests
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
