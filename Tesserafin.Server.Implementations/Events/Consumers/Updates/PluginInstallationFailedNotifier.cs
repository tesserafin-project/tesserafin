using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Common.Updates;
using Tesserafin.Controller.Events;
using Tesserafin.Controller.Session;
using Tesserafin.Model.Session;

namespace Tesserafin.Server.Implementations.Events.Consumers.Updates
{
    /// <summary>
    /// Notifies admin users when a plugin installation fails.
    /// </summary>
    public class PluginInstallationFailedNotifier : IEventConsumer<InstallationFailedEventArgs>
    {
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginInstallationFailedNotifier"/> class.
        /// </summary>
        /// <param name="sessionManager">The session manager.</param>
        public PluginInstallationFailedNotifier(ISessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(InstallationFailedEventArgs eventArgs)
        {
            await _sessionManager.SendMessageToAdminSessions(SessionMessageType.PackageInstallationFailed, eventArgs.InstallationInfo, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
