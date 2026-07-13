#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Library;
using Reefin.Controller.Playlists;
using Reefin.Controller.Sorting;
using Reefin.Data;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Entities;
using Reefin.Database.Implementations.Enums;
using Reefin.Extensions;
using Reefin.Model.Library;

namespace Reefin.Server.Core.Library
{
    /// <summary>
    /// Scopes <see cref="InternalItemsQuery"/> instances to a set of parents or to a user's visible
    /// top-level views. This is the sole production implementation of
    /// <see cref="IItemQueryScopeService"/>.
    /// </summary>
    /// <remarks>
    /// Extracted in PR85b from <see cref="LibraryManager"/>'s private
    /// <c>SetTopParentIdsOrAncestors</c>/<c>AddUserToQuery</c>/<c>GetTopParentIdsForQuery</c>
    /// helpers, copied verbatim (not moved) - <see cref="LibraryManager"/> keeps its own copies
    /// during the transition to avoid re-forming a DI cycle
    /// (<c>LibraryManager -&gt; IItemQueryService -&gt; IUserViewManager -&gt; ILibraryManager</c>);
    /// see <c>docs/pr85b-item-query-scope-service.md</c>. This service is independent of
    /// <see cref="ILibraryManager"/> in its own implementation, but still depends on a cyclic DI
    /// sub-graph via <see cref="IUserViewManager"/> (one of the central nodes of the query/user-views/
    /// channel cycle); it is not a fully cycle-free leaf. It can still be consumed by
    /// <c>IItemQueryService</c> because it holds no direct <see cref="ILibraryManager"/> reference.
    /// </remarks>
    internal sealed class ItemQueryScopeService : IItemQueryScopeService
    {
        private readonly IItemLookupService _itemLookupService;
        private readonly IUserViewManager _userViewManager;
        private readonly IItemSortService _itemSortService;
        private readonly IUserRootFolderProvider _rootFolderProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemQueryScopeService"/> class.
        /// </summary>
        /// <param name="itemLookupService">Used to resolve view display/parent ids in <c>GetTopParentIdsForQuery</c>.</param>
        /// <param name="userViewManager">Used to resolve a user's visible views in <see cref="AddUserToQuery"/>.</param>
        /// <param name="itemSortService">Used to sort the root folder's children in the grouped-folders branch.</param>
        /// <param name="rootFolderProvider">Used to obtain the user root folder in the grouped-folders branch.</param>
        public ItemQueryScopeService(
            IItemLookupService itemLookupService,
            IUserViewManager userViewManager,
            IItemSortService itemSortService,
            IUserRootFolderProvider rootFolderProvider)
        {
            _itemLookupService = itemLookupService;
            _userViewManager = userViewManager;
            _itemSortService = itemSortService;
            _rootFolderProvider = rootFolderProvider;
        }

        /// <inheritdoc />
        public void SetTopParentIdsOrAncestors(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents)
        {
            if (parents.All(i => i is ICollectionFolder || i is UserView))
            {
                // Optimize by querying against top level views
                query.TopParentIds = parents.SelectMany(i => GetTopParentIdsForQuery(i, query.User)).ToArray();

                // Prevent searching in all libraries due to empty filter
                if (query.TopParentIds.Length == 0)
                {
                    query.TopParentIds = [Guid.NewGuid()];
                }
            }
            else if (parents.Count == 1 && parents.First() is Folder folder
                && (folder is Playlist || folder is BoxSet)
                && folder.LinkedChildren.Length > 0)
            {
                // Playlists and BoxSets store their contents in LinkedChildren and never
                // populate AncestorIds for those items, so a recursive AncestorIds query
                // would return zero rows. Resolve to the linked child IDs up front and
                // route through the existing indexed ItemIds filter.
                query.ItemIds = folder.LinkedChildren
                    .Where(lc => lc.ItemId.HasValue && !lc.ItemId.Value.IsEmpty())
                    .Select(lc => lc.ItemId!.Value)
                    .ToArray();

                // Empty linked-children should still return empty rather than scanning everything.
                if (query.ItemIds.Length == 0)
                {
                    query.ItemIds = [Guid.NewGuid()];
                }
            }
            else
            {
                // We need to be able to query from any arbitrary ancestor up the tree
                query.AncestorIds = parents.SelectMany(i => i.GetIdsForAncestorQuery()).ToArray();

                // Prevent searching in all libraries due to empty filter
                if (query.AncestorIds.Length == 0)
                {
                    query.AncestorIds = [Guid.NewGuid()];
                }
            }

            query.Parent = null;
        }

        /// <inheritdoc />
        public void AddUserToQuery(InternalItemsQuery query, User user, bool allowExternalContent = true)
        {
            if (query.User is null)
            {
                query.SetUser(user);
            }

            if (query.AncestorIds.Length == 0 &&
                query.ParentId.IsEmpty() &&
                query.ChannelIds.Count == 0 &&
                query.TopParentIds.Length == 0 &&
                string.IsNullOrEmpty(query.AncestorWithPresentationUniqueKey) &&
                string.IsNullOrEmpty(query.SeriesPresentationUniqueKey) &&
                query.ItemIds.Length == 0 &&
                query.OwnerIds.Length == 0)
            {
                var userViews = _userViewManager.GetUserViews(new UserViewQuery
                {
                    User = user,
                    IncludeHidden = true,
                    IncludeExternalContent = allowExternalContent
                });

                query.TopParentIds = userViews.SelectMany(i => GetTopParentIdsForQuery(i, user)).ToArray();

                // Prevent searching in all libraries due to empty filter
                if (query.TopParentIds.Length == 0)
                {
                    query.TopParentIds = [Guid.NewGuid()];
                }
            }
        }

        private IEnumerable<Guid> GetTopParentIdsForQuery(BaseItem item, User? user)
        {
            if (item is UserView view)
            {
                if (view.ViewType == CollectionType.livetv)
                {
                    return [view.Id];
                }

                // Translate view into folders
                if (!view.DisplayParentId.IsEmpty())
                {
                    var displayParent = _itemLookupService.GetItemById(view.DisplayParentId);
                    if (displayParent is not null)
                    {
                        return GetTopParentIdsForQuery(displayParent, user);
                    }

                    return [];
                }

                if (!view.ParentId.IsEmpty())
                {
                    var displayParent = _itemLookupService.GetItemById(view.ParentId);
                    if (displayParent is not null)
                    {
                        return GetTopParentIdsForQuery(displayParent, user);
                    }

                    return [];
                }

                // Handle grouping
                if (user is not null && view.ViewType != CollectionType.unknown && UserView.IsEligibleForGrouping(view.ViewType)
                    && user.GetPreference(PreferenceKind.GroupedFolders).Length > 0)
                {
                    return _rootFolderProvider.GetUserRootFolder()
                        .GetChildren(user, true, null, _itemSortService)
                        .OfType<CollectionFolder>()
                        .Where(i => i.CollectionType is null || i.CollectionType == view.ViewType)
                        .Where(i => user.IsFolderGrouped(i.Id))
                        .SelectMany(i => GetTopParentIdsForQuery(i, user));
                }

                return [];
            }

            if (item is CollectionFolder collectionFolder)
            {
                return collectionFolder.PhysicalFolderIds;
            }

            var topParent = GetTopParentViaLookup(item);
            if (topParent is not null)
            {
                return [topParent.Id];
            }

            return [];
        }

        // Walks to the top-level parent using IItemLookupService instead of BaseItem.GetTopParent(),
        // which would reach the static BaseItem.LibraryManager. Single consumer, so it stays a private
        // helper here rather than a shared hierarchy service (see docs/pr90-*, and the removed
        // IItemHierarchyService in PR83).
        private BaseItem? GetTopParentViaLookup(BaseItem item)
        {
            if (item.IsTopParentVia(_itemLookupService))
            {
                return item;
            }

            return item.GetParents(_itemLookupService)
                .FirstOrDefault(parent => parent.IsTopParentVia(_itemLookupService));
        }
    }
}
