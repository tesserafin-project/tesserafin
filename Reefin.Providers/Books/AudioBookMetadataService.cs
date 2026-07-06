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

namespace Reefin.Providers.Books;

/// <summary>
/// Service to manage audiobook metadata.
/// </summary>
public class AudioBookMetadataService : MetadataService<AudioBook, SongInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AudioBookMetadataService"/> class.
    /// </summary>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="externalDataManager">Instance of the <see cref="IExternalDataManager"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    public AudioBookMetadataService(
        IServerConfigurationManager serverConfigurationManager,
        ILogger<AudioBookMetadataService> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryManager libraryManager,
        IExternalDataManager externalDataManager,
        IItemRepository itemRepository)
        : base(serverConfigurationManager, logger, providerManager, fileSystem, libraryManager, externalDataManager, itemRepository)
    {
    }

    /// <inheritdoc />
    protected override void MergeData(
        MetadataResult<AudioBook> source,
        MetadataResult<AudioBook> target,
        MetadataField[] lockedFields,
        bool replaceData,
        bool mergeMetadataSettings)
    {
        base.MergeData(source, target, lockedFields, replaceData, mergeMetadataSettings);

        var sourceItem = source.Item;
        var targetItem = target.Item;

        if (replaceData || targetItem.Artists.Count == 0)
        {
            targetItem.Artists = sourceItem.Artists;
        }

        if (replaceData || string.IsNullOrEmpty(targetItem.Album))
        {
            targetItem.Album = sourceItem.Album;
        }
    }
}
