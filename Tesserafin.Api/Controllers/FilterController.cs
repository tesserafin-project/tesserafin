using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Querying;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// Filters controller.
/// </summary>
[Route("")]
[Authorize]
public class FilterController : BaseTesserafinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILocalizationManager _localization;
    private readonly IMediaStreamLanguageService _mediaStreamLanguageService;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="mediaStreamLanguageService">Instance of the <see cref="IMediaStreamLanguageService"/> interface.</param>
    public FilterController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILocalizationManager localization,
        IMediaStreamLanguageService mediaStreamLanguageService)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _localization = localization;
        _mediaStreamLanguageService = mediaStreamLanguageService;
    }

    /// <summary>
    /// Gets legacy query filters.
    /// </summary>
    /// <param name="userId">Optional. User id.</param>
    /// <param name="parentId">Optional. Parent id.</param>
    /// <param name="includeItemTypes">Optional. If specified, results will be filtered based on item type. This allows multiple, comma delimited.</param>
    /// <param name="mediaTypes">Optional. Filter by MediaType. Allows multiple, comma delimited.</param>
    /// <response code="200">Legacy filters retrieved.</response>
    /// <returns>Legacy query filters.</returns>
    [HttpGet("Items/Filters")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryFiltersLegacy> GetQueryFiltersLegacy(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] MediaType[] mediaTypes)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);

        BaseItem? item = null;
        if (includeItemTypes.Length != 1
            || !(includeItemTypes[0] == BaseItemKind.Trailer
                 || includeItemTypes[0] == BaseItemKind.Program))
        {
            item = _libraryManager.GetParentItem(parentId, user?.Id);
        }

        if (item is not Folder folder)
        {
            return new QueryFiltersLegacy();
        }

        var query = new InternalItemsQuery(user)
        {
            MediaTypes = mediaTypes,
            IncludeItemTypes = includeItemTypes,
            Recursive = true,
            EnableTotalRecordCount = false,
            AncestorIds = [folder.Id],
            DtoOptions = new DtoOptions
            {
                Fields = [],
                EnableImages = false,
                EnableUserData = false
            }
        };

        return _libraryManager.GetQueryFiltersLegacy(query);
    }

    /// <summary>
    /// Gets query filters.
    /// </summary>
    /// <param name="userId">Optional. User id.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific item or folder. Omit to use the root.</param>
    /// <param name="includeItemTypes">Optional. If specified, results will be filtered based on item type. This allows multiple, comma delimited.</param>
    /// <param name="isAiring">Optional. Is item airing.</param>
    /// <param name="isMovie">Optional. Is item movie.</param>
    /// <param name="isSports">Optional. Is item sports.</param>
    /// <param name="isKids">Optional. Is item kids.</param>
    /// <param name="isNews">Optional. Is item news.</param>
    /// <param name="isSeries">Optional. Is item series.</param>
    /// <param name="recursive">Optional. Search recursive.</param>
    /// <response code="200">Filters retrieved.</response>
    /// <returns>Query filters.</returns>
    [HttpGet("Items/Filters2")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueryFilters> GetQueryFilters(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? parentId,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes,
        [FromQuery] bool? isAiring,
        [FromQuery] bool? isMovie,
        [FromQuery] bool? isSports,
        [FromQuery] bool? isKids,
        [FromQuery] bool? isNews,
        [FromQuery] bool? isSeries,
        [FromQuery] bool? recursive)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);

        BaseItem? parentItem = null;
        if (includeItemTypes.Length == 1
            && (includeItemTypes[0] == BaseItemKind.Trailer
                || includeItemTypes[0] == BaseItemKind.Program))
        {
            parentItem = null;
        }
        else if (parentId.HasValue)
        {
            parentItem = _libraryManager.GetItemById<BaseItem>(parentId.Value);
        }

        var filters = new QueryFilters();
        var genreQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = includeItemTypes,
            DtoOptions = new DtoOptions
            {
                Fields = Array.Empty<ItemFields>(),
                EnableImages = false,
                EnableUserData = false
            },
            IsAiring = isAiring,
            IsMovie = isMovie,
            IsSports = isSports,
            IsKids = isKids,
            IsNews = isNews,
            IsSeries = isSeries
        };

        if ((recursive ?? true) || parentItem is UserView || parentItem is ICollectionFolder)
        {
            genreQuery.AncestorIds = parentItem is null ? Array.Empty<Guid>() : new[] { parentItem.Id };
        }
        else
        {
            genreQuery.Parent = parentItem;
        }

        if (includeItemTypes.Length == 1
            && (includeItemTypes[0] == BaseItemKind.MusicAlbum
                || includeItemTypes[0] == BaseItemKind.MusicVideo
                || includeItemTypes[0] == BaseItemKind.MusicArtist
                || includeItemTypes[0] == BaseItemKind.Audio))
        {
            filters.Genres = _libraryManager.GetMusicGenres(genreQuery).Items.Select(i => new NameGuidPair
            {
                Name = i.Item.Name,
                Id = i.Item.Id
            }).ToArray();
        }
        else
        {
            filters.Genres = _libraryManager.GetGenres(genreQuery).Items.Select(i => new NameGuidPair
            {
                Name = i.Item.Name,
                Id = i.Item.Id
            }).ToArray();
        }

        if (includeItemTypes.Contains(BaseItemKind.Movie) || includeItemTypes.Contains(BaseItemKind.Series))
        {
            filters.AudioLanguages = _mediaStreamLanguageService
                .GetMediaStreamLanguages(MediaStreamType.Audio)
                .Select(language =>
                {
                    var culture = _localization.FindLanguageInfo(language);
                    return new NameValuePair
                    {
                        Name = culture is null ? language : $"{culture.DisplayName} ({language})",
                        Value = language
                    };
                })
                .OrderBy(l => l.Name)
                .ToArray();
            filters.SubtitleLanguages = _mediaStreamLanguageService
                .GetMediaStreamLanguages(MediaStreamType.Subtitle)
                .Select(language =>
                {
                    var culture = _localization.FindLanguageInfo(language);
                    return new NameValuePair
                    {
                        Name = culture is null ? language : $"{culture.DisplayName} ({language})",
                        Value = language
                    };
                })
                .OrderBy(l => l.Name)
                .ToArray();
        }

        return filters;
    }
}
