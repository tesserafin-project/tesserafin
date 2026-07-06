using System.Threading;
using Reefin.Controller.Session;
using Reefin.Model.SyncPlay;

namespace Reefin.Controller.SyncPlay.PlaybackRequests
{
    /// <summary>
    /// Class UnpauseGroupRequest.
    /// </summary>
    public class UnpauseGroupRequest : AbstractPlaybackRequest
    {
        /// <inheritdoc />
        public override PlaybackRequestType Action { get; } = PlaybackRequestType.Unpause;

        /// <inheritdoc />
        public override void Apply(IGroupStateContext context, IGroupState state, SessionInfo session, CancellationToken cancellationToken)
        {
            state.HandleRequest(this, context, state.Type, session, cancellationToken);
        }
    }
}
