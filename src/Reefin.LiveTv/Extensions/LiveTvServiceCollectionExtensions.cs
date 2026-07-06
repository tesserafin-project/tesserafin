using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Reefin.LiveTv.Channels;
using Reefin.LiveTv.Guide;
using Reefin.LiveTv.IO;
using Reefin.LiveTv.Listings;
using Reefin.LiveTv.Recordings;
using Reefin.LiveTv.Timers;
using Reefin.LiveTv.TunerHosts;
using Reefin.LiveTv.TunerHosts.HdHomerun;
using Reefin.Model.IO;

namespace Reefin.LiveTv.Extensions;

/// <summary>
/// Live TV extensions for <see cref="IServiceCollection"/>.
/// </summary>
public static class LiveTvServiceCollectionExtensions
{
    /// <summary>
    /// Adds Live TV services to the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    public static void AddLiveTvServices(this IServiceCollection services)
    {
        services.AddSingleton<LiveTvDtoService>();
        services.AddSingleton<TimerManager>();
        services.AddSingleton<SeriesTimerManager>();
        services.AddSingleton<RecordingsMetadataManager>();

        services.AddSingleton<ILiveTvManager, LiveTvManager>();
        services.AddSingleton<IChannelManager, ChannelManager>();
        services.AddSingleton<IStreamHelper, StreamHelper>();
        services.AddSingleton<ITunerHostManager, TunerHostManager>();
        services.AddSingleton<IListingsManager, ListingsManager>();
        services.AddSingleton<IGuideManager, GuideManager>();
        services.AddSingleton<IRecordingsManager, RecordingsManager>();

        services.AddSingleton<ILiveTvService, DefaultLiveTvService>();
        services.AddSingleton<ITunerHost, HdHomerunHost>();
        services.AddSingleton<ITunerHost, M3UTunerHost>();
        services.AddSingleton<SchedulesDirect>();
        services.AddSingleton<IListingsProvider>(s => s.GetRequiredService<SchedulesDirect>());
        services.AddSingleton<ISchedulesDirectService>(s => s.GetRequiredService<SchedulesDirect>());
        services.AddSingleton<IListingsProvider, XmlTvListingsProvider>();
    }
}
