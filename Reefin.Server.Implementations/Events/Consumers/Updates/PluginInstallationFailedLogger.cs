using System;
using System.Globalization;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using Reefin.Common.Updates;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Activity;
using Reefin.Model.Globalization;
using Reefin.Model.Notifications;

namespace Reefin.Server.Implementations.Events.Consumers.Updates
{
    /// <summary>
    /// Creates an entry in the activity log when a package installation fails.
    /// </summary>
    public class PluginInstallationFailedLogger : IEventConsumer<InstallationFailedEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginInstallationFailedLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public PluginInstallationFailedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(InstallationFailedEventArgs eventArgs)
        {
            await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("NameInstallFailed"),
                    eventArgs.InstallationInfo.Name),
                NotificationType.InstallationFailed.ToString(),
                Guid.Empty)
            {
                ShortOverview = string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("VersionNumber"),
                    eventArgs.InstallationInfo.Version),
                Overview = eventArgs.Exception.Message
            }).ConfigureAwait(false);
        }
    }
}
