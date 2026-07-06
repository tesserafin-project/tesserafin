using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Reefin.Controller.Dto;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Library;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Enums;
using Reefin.Model.Configuration;

namespace Reefin.Server.Core.Library.SimilarItems;

/// <summary>
/// Provides similar items for music albums.
/// </summary>
public class MusicAlbumSimilarItemsProvider : ILocalSimilarItemsProvider<MusicAlbum>
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicAlbumSimilarItemsProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    public MusicAlbumSimilarItemsProvider(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public string Name => "Local Genre/Tag";

    /// <inheritdoc/>
    public MetadataPluginType Type => MetadataPluginType.LocalSimilarityProvider;

    /// <inheritdoc/>
    public Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(MusicAlbum item, SimilarItemsQuery query, CancellationToken cancellationToken)
    {
        var internalQuery = new InternalItemsQuery(query.User)
        {
            Genres = item.Genres,
            Tags = item.Tags,
            Limit = query.Limit,
            DtoOptions = query.DtoOptions ?? new DtoOptions(),
            ExcludeItemIds = [.. query.ExcludeItemIds],
            ExcludeArtistIds = [.. query.ExcludeArtistIds],
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            EnableGroupByMetadataKey = false,
            EnableTotalRecordCount = true,
            OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)]
        };

        return Task.FromResult(_libraryManager.GetItemList(internalQuery));
    }
}
