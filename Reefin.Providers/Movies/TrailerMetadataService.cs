using System.Linq;
using Microsoft.Extensions.Logging;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.IO;
using Reefin.Controller.Library;
using Reefin.Controller.Persistence;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.IO;
using Reefin.Providers.Manager;

namespace Reefin.Providers.Movies;

/// <summary>
/// Service to manage trailer metadata.
/// </summary>
public class TrailerMetadataService : MetadataService<Trailer, TrailerInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerMetadataService"/> class.
    /// </summary>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
    /// <param name="externalDataManager">Instance of the <see cref="IExternalDataManager"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    public TrailerMetadataService(
        IServerConfigurationManager serverConfigurationManager,
        ILogger<TrailerMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryManager libraryManager,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository)
        : base(serverConfigurationManager, logger, providerManager, fileSystem, libraryManager, itemNamingService, externalDataManager, itemRepository)
    {
    }

    /// <inheritdoc />
    protected override void MergeData(MetadataResult<Trailer> source, MetadataResult<Trailer> target, MetadataField[] lockedFields, bool replaceData, bool mergeMetadataSettings)
    {
        base.MergeData(source, target, lockedFields, replaceData, mergeMetadataSettings);

        if (replaceData || target.Item.TrailerTypes.Length == 0)
        {
            target.Item.TrailerTypes = source.Item.TrailerTypes;
        }
        else
        {
            target.Item.TrailerTypes = target.Item.TrailerTypes.Concat(source.Item.TrailerTypes).Distinct().ToArray();
        }
    }
}
