using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Common.Updates;
using Tesserafin.Controller.Events;
using Tesserafin.Controller.Events.Authentication;
using Tesserafin.Controller.Events.Session;
using Tesserafin.Controller.Events.Updates;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Lyrics;
using Tesserafin.Controller.Subtitles;
using Tesserafin.Data.Events.System;
using Tesserafin.Data.Events.Users;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Implementations.Events.Consumers.Library;
using Tesserafin.Server.Implementations.Events.Consumers.Security;
using Tesserafin.Server.Implementations.Events.Consumers.Session;
using Tesserafin.Server.Implementations.Events.Consumers.System;
using Tesserafin.Server.Implementations.Events.Consumers.Updates;
using Tesserafin.Server.Implementations.Events.Consumers.Users;

namespace Tesserafin.Server.Implementations.Events
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
