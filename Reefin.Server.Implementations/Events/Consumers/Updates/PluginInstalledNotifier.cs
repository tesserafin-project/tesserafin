using System.Threading;
using System.Threading.Tasks;
using Reefin.Controller.Events;
using Reefin.Controller.Events.Updates;
using Reefin.Controller.Session;
using Reefin.Model.Session;

namespace Reefin.Server.Implementations.Events.Consumers.Updates
{
    /// <summary>
    /// Notifies admin users when a plugin is installed.
    /// </summary>
    public class PluginInstalledNotifier : IEventConsumer<PluginInstalledEventArgs>
    {
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginInstalledNotifier"/> class.
        /// </summary>
        /// <param name="sessionManager">The session manager.</param>
        public PluginInstalledNotifier(ISessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(PluginInstalledEventArgs eventArgs)
        {
            await _sessionManager.SendMessageToAdminSessions(SessionMessageType.PackageInstallationCompleted, eventArgs.Argument, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
