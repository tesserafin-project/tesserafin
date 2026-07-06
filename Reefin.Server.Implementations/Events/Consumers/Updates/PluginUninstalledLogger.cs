using System;
using System.Globalization;
using System.Threading.Tasks;
using Reefin.Controller.Events;
using Reefin.Controller.Events.Updates;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Activity;
using Reefin.Model.Globalization;
using Reefin.Model.Notifications;

namespace Reefin.Server.Implementations.Events.Consumers.Updates
{
    /// <summary>
    /// Creates an entry in the activity log when a plugin is uninstalled.
    /// </summary>
    public class PluginUninstalledLogger : IEventConsumer<PluginUninstalledEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginUninstalledLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public PluginUninstalledLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(PluginUninstalledEventArgs eventArgs)
        {
            await _activityManager.CreateAsync(new ActivityLog(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        _localizationManager.GetServerLocalizedString("PluginUninstalledWithName"),
                        eventArgs.Argument.Name),
                    NotificationType.PluginUninstalled.ToString(),
                    Guid.Empty))
                .ConfigureAwait(false);
        }
    }
}
