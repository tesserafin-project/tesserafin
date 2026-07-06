using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Events.Session;
using MediaBrowser.Controller.Events.Updates;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;
using Reefin.Common.Updates;
using Reefin.Data.Events.System;
using Reefin.Data.Events.Users;
using Reefin.Model.Tasks;
using Reefin.Server.Implementations.Events.Consumers.Library;
using Reefin.Server.Implementations.Events.Consumers.Security;
using Reefin.Server.Implementations.Events.Consumers.Session;
using Reefin.Server.Implementations.Events.Consumers.System;
using Reefin.Server.Implementations.Events.Consumers.Updates;
using Reefin.Server.Implementations.Events.Consumers.Users;

namespace Reefin.Server.Implementations.Events
{
    /// <summary>
    /// A class containing extensions to <see cref="IServiceCollection"/> for eventing.
    /// </summary>
    public static class EventingServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the event services to the service collection.
        /// </summary>
        /// <param name="collection">The service collection.</param>
        public static void AddEventServices(this IServiceCollection collection)
        {
            // Library consumers
            collection.AddScoped<IEventConsumer<LyricDownloadFailureEventArgs>, LyricDownloadFailureLogger>();
            collection.AddScoped<IEventConsumer<SubtitleDownloadFailureEventArgs>, SubtitleDownloadFailureLogger>();

            // Security consumers
            collection.AddScoped<IEventConsumer<AuthenticationRequestEventArgs>, AuthenticationFailedLogger>();
            collection.AddScoped<IEventConsumer<AuthenticationResultEventArgs>, AuthenticationSucceededLogger>();

            // Session consumers
            collection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, PlaybackStartLogger>();
            collection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, PlaybackStopLogger>();
            collection.AddScoped<IEventConsumer<SessionEndedEventArgs>, SessionEndedLogger>();
            collection.AddScoped<IEventConsumer<SessionStartedEventArgs>, SessionStartedLogger>();

            // System consumers
            collection.AddScoped<IEventConsumer<PendingRestartEventArgs>, PendingRestartNotifier>();
            collection.AddScoped<IEventConsumer<TaskCompletionEventArgs>, TaskCompletedLogger>();
            collection.AddScoped<IEventConsumer<TaskCompletionEventArgs>, TaskCompletedNotifier>();

            // Update consumers
            collection.AddScoped<IEventConsumer<PluginInstallationCancelledEventArgs>, PluginInstallationCancelledNotifier>();
            collection.AddScoped<IEventConsumer<InstallationFailedEventArgs>, PluginInstallationFailedLogger>();
            collection.AddScoped<IEventConsumer<InstallationFailedEventArgs>, PluginInstallationFailedNotifier>();
            collection.AddScoped<IEventConsumer<PluginInstalledEventArgs>, PluginInstalledLogger>();
            collection.AddScoped<IEventConsumer<PluginInstalledEventArgs>, PluginInstalledNotifier>();
            collection.AddScoped<IEventConsumer<PluginInstallingEventArgs>, PluginInstallingNotifier>();
            collection.AddScoped<IEventConsumer<PluginUninstalledEventArgs>, PluginUninstalledLogger>();
            collection.AddScoped<IEventConsumer<PluginUninstalledEventArgs>, PluginUninstalledNotifier>();
            collection.AddScoped<IEventConsumer<PluginUpdatedEventArgs>, PluginUpdatedLogger>();

            // User consumers
            collection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserCreatedLogger>();
            collection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedLogger>();
            collection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedNotifier>();
            collection.AddScoped<IEventConsumer<UserLockedOutEventArgs>, UserLockedOutLogger>();
            collection.AddScoped<IEventConsumer<UserPasswordChangedEventArgs>, UserPasswordChangedLogger>();
            collection.AddScoped<IEventConsumer<UserUpdatedEventArgs>, UserUpdatedNotifier>();
        }
    }
}
