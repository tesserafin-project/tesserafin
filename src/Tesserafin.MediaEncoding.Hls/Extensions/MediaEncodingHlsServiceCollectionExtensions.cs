using System;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.MediaEncoding.Hls.Cache;
using Tesserafin.MediaEncoding.Hls.Extractors;
using Tesserafin.MediaEncoding.Hls.Playlist;

namespace Tesserafin.MediaEncoding.Hls.Extensions;

/// <summary>
/// Extensions for the <see cref="IServiceCollection"/> interface.
/// </summary>
public static class MediaEncodingHlsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the hls playlist generators to the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="serviceCollection">An instance of the <see cref="IServiceCollection"/> interface.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddHlsPlaylistGenerator(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingletonWithDecorator(typeof(FfProbeKeyframeExtractor));
        serviceCollection.AddSingletonWithDecorator(typeof(MatroskaKeyframeExtractor));
        serviceCollection.AddSingleton<IDynamicHlsPlaylistGenerator, DynamicHlsPlaylistGenerator>();
        return serviceCollection;
    }

    private static void AddSingletonWithDecorator(this IServiceCollection serviceCollection, Type type)
    {
        serviceCollection.AddSingleton<IKeyframeExtractor>(serviceProvider =>
        {
            var extractor = ActivatorUtilities.CreateInstance(serviceProvider, type);
            var decorator = ActivatorUtilities.CreateInstance<CacheDecorator>(serviceProvider, extractor);
            return decorator;
        });
    }
}
