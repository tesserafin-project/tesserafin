#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Entities.TV;
using Reefin.Controller.Library;
using Reefin.Controller.TV;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Querying;

namespace Reefin.Server.Core.Library
{
    public class ItemQueryService : IItemQueryService
    {
        private readonly IChannelManager _channelManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserViewManager _userViewManager;
        private readonly ITVSeriesManager _tvSeriesManager;

        public ItemQueryService(IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            _channelManager = channelManager;
            _collectionManager = collectionManager;
            _userViewManager = userViewManager;
            _tvSeriesManager = tvSeriesManager;
        }

        public QueryResult<BaseItem> GetItems(Folder folder, InternalItemsQuery query)
        {
            if (!CanUseRawQueryItemsFastPath(folder, query))
            {
                return folder.GetItems(query, _channelManager, _collectionManager, _userViewManager, _tvSeriesManager);
            }

            return PostFilterAndSort(folder, folder.GetRawQueryItems(query), query);
        }

        public IReadOnlyList<BaseItem> GetItemList(Folder folder, InternalItemsQuery query)
        {
            if (!CanUseRawQueryItemsFastPath(folder, query))
            {
                return folder.GetItemList(query, _channelManager, _collectionManager, _userViewManager, _tvSeriesManager);
            }

            // Mirrors Folder.GetItemList's own unconditional mutation before it delegates further.
            query.EnableTotalRecordCount = false;
            return PostFilterAndSort(folder, folder.GetRawQueryItems(query), query).Items;
        }

        // GetRawQueryItems + PostFilterAndSort is only equivalent to the full GetItems/GetItemList
        // pipeline when none of the branches that precede or replace it in Folder are in play:
        // folder.SupportsRawQueryItems (PR25) rules out the 6 known GetItemsInternal overrides that
        // build items differently; query.ItemIds mirrors Folder.GetItems/GetItemList's own early
        // return (LibraryManager.GetItemsResult/GetItemList, never reaches GetItemsInternal at all);
        // SourceType.Channel and query.Recursive mirror GetItemsInternal's own first two checks.
        private static bool CanUseRawQueryItemsFastPath(Folder folder, InternalItemsQuery query)
            => folder.SupportsRawQueryItems
                && query.ItemIds.Length == 0
                && folder.SourceType != SourceType.Channel
                && !query.Recursive;

        // Relocation of Folder.PostFilterAndSort (and its private helpers) per the major rewrite
        // plan's point 5 (PR23/N): not wired into GetItems/GetItemList yet (Folder keeps its own
        // copy, GetItemsInternal untouched) - this is the parity-tested standalone version, wiring
        // is deferred to PR24 once Folder exposes a raw-children primitive (avoids duplicating the
        // child-fetch logic to reach that wiring point). BaseItem.ConfigurationManager/LibraryManager
        // read here are the same statics Folder.PostFilterAndSort itself reads via instance property
        // access - behavior, not just code, is reproduced.
        internal QueryResult<BaseItem> PostFilterAndSort(BaseItem queryParent, IEnumerable<BaseItem> items, InternalItemsQuery query)
        {
            var user = query.User;

            if (user is not null)
            {
                items = CollapseBoxSetItemsIfNeeded(items, query, queryParent, user);
                items = ApplyNameFilter(items, query);
            }

            var filteredItems = items as IReadOnlyList<BaseItem> ?? items.ToList();
            var result = UserViewBuilder.SortAndPage(filteredItems, null, query, BaseItem.LibraryManager);

            if (query.EnableTotalRecordCount)
            {
                result.TotalRecordCount = filteredItems.Count;
            }

            return result;
        }

        private static IEnumerable<BaseItem> ApplyNameFilter(IEnumerable<BaseItem> items, InternalItemsQuery query)
        {
            if (!string.IsNullOrWhiteSpace(query.NameStartsWith))
            {
                items = items.Where(i => i.SortName.StartsWith(query.NameStartsWith, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.NameStartsWithOrGreater))
            {
                items = items.Where(i => string.Compare(i.SortName, query.NameStartsWithOrGreater, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrWhiteSpace(query.NameLessThan))
            {
                items = items.Where(i => string.Compare(i.SortName, query.NameLessThan, StringComparison.OrdinalIgnoreCase) < 0);
            }

            return items;
        }

        private IEnumerable<BaseItem> CollapseBoxSetItemsIfNeeded(
            IEnumerable<BaseItem> items,
            InternalItemsQuery query,
            BaseItem queryParent,
            User user)
        {
            ArgumentNullException.ThrowIfNull(items);

            if (!CollapseBoxSetItems(query, queryParent, user))
            {
                return items;
            }

            var config = BaseItem.ConfigurationManager.Configuration;

            bool collapseMovies = config.EnableGroupingMoviesIntoCollections;
            bool collapseSeries = config.EnableGroupingShowsIntoCollections;

            if (user is null || (collapseMovies && collapseSeries))
            {
                return _collectionManager.CollapseItemsWithinBoxSets(items, user);
            }

            if (!collapseMovies && !collapseSeries)
            {
                return items;
            }

            var collapsibleItems = new List<BaseItem>();
            var remainingItems = new List<BaseItem>();

            foreach (var item in items)
            {
                if ((collapseMovies && item is Movie) || (collapseSeries && item is Series))
                {
                    collapsibleItems.Add(item);
                }
                else
                {
                    remainingItems.Add(item);
                }
            }

            if (collapsibleItems.Count == 0)
            {
                return remainingItems;
            }

            var collapsedItems = _collectionManager.CollapseItemsWithinBoxSets(collapsibleItems, user);

            return collapsedItems.Concat(remainingItems);
        }

        private static bool CollapseBoxSetItems(
            InternalItemsQuery query,
            BaseItem queryParent,
            User user)
        {
            // Could end up stuck in a loop like this
            if (queryParent is BoxSet)
            {
                return false;
            }

            if (queryParent is Season)
            {
                return false;
            }

            if (queryParent is MusicAlbum)
            {
                return false;
            }

            if (queryParent is MusicArtist)
            {
                return false;
            }

            var param = query.CollapseBoxSetItems;
            if (param.HasValue)
            {
                return param.Value && AllowBoxSetCollapsing(query);
            }

            var config = BaseItem.ConfigurationManager.Configuration;

            bool queryHasMovies = query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(BaseItemKind.Movie);
            bool queryHasSeries = query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(BaseItemKind.Series);

            bool collapseMovies = config.EnableGroupingMoviesIntoCollections;
            bool collapseSeries = config.EnableGroupingShowsIntoCollections;

            if (user is not null)
            {
                bool canCollapse = (queryHasMovies && collapseMovies) || (queryHasSeries && collapseSeries);
                return canCollapse && AllowBoxSetCollapsing(query);
            }

            return (queryHasMovies || queryHasSeries) && AllowBoxSetCollapsing(query);
        }

        private static bool AllowBoxSetCollapsing(InternalItemsQuery request)
        {
            if (request.IsFavorite.HasValue)
            {
                return false;
            }

            if (request.IsFavoriteOrLiked.HasValue)
            {
                return false;
            }

            if (request.IsLiked.HasValue)
            {
                return false;
            }

            if (request.IsPlayed.HasValue)
            {
                return false;
            }

            if (request.IsResumable.HasValue)
            {
                return false;
            }

            if (request.IsFolder.HasValue)
            {
                return false;
            }

            if (request.Genres.Count > 0)
            {
                return false;
            }

            if (request.GenreIds.Count > 0)
            {
                return false;
            }

            if (request.HasImdbId.HasValue)
            {
                return false;
            }

            if (request.HasOfficialRating.HasValue)
            {
                return false;
            }

            if (request.HasOverview.HasValue)
            {
                return false;
            }

            if (request.HasParentalRating.HasValue)
            {
                return false;
            }

            if (request.HasSpecialFeature.HasValue)
            {
                return false;
            }

            if (request.HasSubtitles.HasValue)
            {
                return false;
            }

            if (request.HasThemeSong.HasValue)
            {
                return false;
            }

            if (request.HasThemeVideo.HasValue)
            {
                return false;
            }

            if (request.HasTmdbId.HasValue)
            {
                return false;
            }

            if (request.HasTrailer.HasValue)
            {
                return false;
            }

            if (request.ImageTypes.Length > 0)
            {
                return false;
            }

            if (request.Is3D.HasValue)
            {
                return false;
            }

            if (request.Is4K.HasValue)
            {
                return false;
            }

            if (request.IsHD.HasValue)
            {
                return false;
            }

            if (request.IsLocked.HasValue)
            {
                return false;
            }

            if (request.IsPlaceHolder.HasValue)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.Person))
            {
                return false;
            }

            if (request.PersonIds.Length > 0)
            {
                return false;
            }

            if (request.ItemIds.Length > 0)
            {
                return false;
            }

            if (request.StudioIds.Length > 0)
            {
                return false;
            }

            if (request.VideoTypes.Length > 0)
            {
                return false;
            }

            if (request.Years.Length > 0)
            {
                return false;
            }

            if (request.Tags.Length > 0)
            {
                return false;
            }

            if (request.OfficialRatings.Length > 0)
            {
                return false;
            }

            if (request.MinIndexNumber.HasValue)
            {
                return false;
            }

            if (request.OrderBy.Any(o =>
                o.OrderBy == ItemSortBy.CommunityRating ||
                o.OrderBy == ItemSortBy.CriticRating ||
                o.OrderBy == ItemSortBy.Runtime))
            {
                return false;
            }

            return true;
        }
    }
}
