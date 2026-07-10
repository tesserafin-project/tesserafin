using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.IO;
using Reefin.Controller.Library;
using Reefin.Controller.Persistence;
using Reefin.Controller.Providers;
using Reefin.Controller.Sorting;
using Reefin.Model.IO;
using Reefin.Providers.Manager;

namespace Reefin.Providers.Music;

/// <summary>
/// Service to manage artist metadata.
/// </summary>
public class ArtistMetadataService : MetadataService<MusicArtist, ArtistInfo>
{
    private readonly IItemSortService _itemSortService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtistMetadataService"/> class.
    /// </summary>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
    /// <param name="externalDataManager">Instance of the <see cref="IExternalDataManager"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    /// <param name="itemSortService">Instance of the <see cref="IItemSortService"/> interface.</param>
    public ArtistMetadataService(
        IServerConfigurationManager serverConfigurationManager,
        ILogger<ArtistMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryManager libraryManager,
        IItemNamingService itemNamingService,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository,
        IItemSortService itemSortService)
        : base(serverConfigurationManager, logger, providerManager, fileSystem, libraryManager, itemNamingService, externalDataManager, itemRepository)
    {
        _itemSortService = itemSortService;
    }

    /// <inheritdoc />
    protected override bool EnableUpdatingGenresFromChildren => true;

    /// <inheritdoc />
    protected override IReadOnlyList<BaseItem> GetChildrenForMetadataUpdates(MusicArtist item)
    {
        return item.IsAccessedByName
            ? item.GetTaggedItems(new InternalItemsQuery
            {
                Recursive = true,
                IsFolder = false
            })
            : item.GetRecursiveChildren(i => i is IHasArtist && !i.IsFolder, _itemSortService);
    }
}
