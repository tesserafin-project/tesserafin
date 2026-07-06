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
    /// Creates an entry in the activity log when a plugin is updated.
    /// </summary>
    public class PluginUpdatedLogger : IEventConsumer<PluginUpdatedEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginUpdatedLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public PluginUpdatedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(PluginUpdatedEventArgs eventArgs)
        {
            await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("PluginUpdatedWithName"),
                    eventArgs.Argument.Name),
                NotificationType.PluginUpdateInstalled.ToString(),
                Guid.Empty)
            {
                ShortOverview = string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("VersionNumber"),
                    eventArgs.Argument.Version),
                Overview = eventArgs.Argument.Changelog
            }).ConfigureAwait(false);
        }
    }
}
