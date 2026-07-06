using System;
using System.Globalization;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using Reefin.Data.Events.Users;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Activity;
using Reefin.Model.Globalization;

namespace Reefin.Server.Implementations.Events.Consumers.Users
{
    /// <summary>
    /// Adds an entry to the activity log when a user is deleted.
    /// </summary>
    public class UserDeletedLogger : IEventConsumer<UserDeletedEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserDeletedLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public UserDeletedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(UserDeletedEventArgs eventArgs)
        {
            await _activityManager.CreateAsync(new ActivityLog(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        _localizationManager.GetServerLocalizedString("UserDeletedWithName"),
                        eventArgs.Argument.Username),
                    "UserDeleted",
                    Guid.Empty))
                .ConfigureAwait(false);
        }
    }
}
