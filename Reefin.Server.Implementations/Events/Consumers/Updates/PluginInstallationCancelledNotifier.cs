using System.Threading;
using System.Threading.Tasks;
using Reefin.Controller.Events;
using Reefin.Controller.Events.Updates;
using Reefin.Controller.Session;
using Reefin.Model.Session;

namespace Reefin.Server.Implementations.Events.Consumers.Updates
{
    /// <summary>
    /// Notifies admin users when a plugin installation is cancelled.
    /// </summary>
    public class PluginInstallationCancelledNotifier : IEventConsumer<PluginInstallationCancelledEventArgs>
    {
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginInstallationCancelledNotifier"/> class.
        /// </summary>
        /// <param name="sessionManager">The session manager.</param>
        public PluginInstallationCancelledNotifier(ISessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(PluginInstallationCancelledEventArgs eventArgs)
        {
            await _sessionManager.SendMessageToAdminSessions(SessionMessageType.PackageInstallationCancelled, eventArgs.Argument, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
