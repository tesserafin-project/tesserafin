using System.Globalization;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;
using Reefin.Data.Events.Users;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Activity;
using Reefin.Model.Globalization;
using Reefin.Model.Notifications;

namespace Reefin.Server.Implementations.Events.Consumers.Users
{
    /// <summary>
    /// Creates an entry in the activity log when a user is locked out.
    /// </summary>
    public class UserLockedOutLogger : IEventConsumer<UserLockedOutEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserLockedOutLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public UserLockedOutLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(UserLockedOutEventArgs eventArgs)
        {
            await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("UserLockedOutWithName"),
                    eventArgs.Argument.Username),
                NotificationType.UserLockedOut.ToString(),
                eventArgs.Argument.Id)
            {
                LogSeverity = LogLevel.Error
            }).ConfigureAwait(false);
        }
    }
}
