#nullable disable

#pragma warning disable CA1721, CA1819, CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Entities.Movies
{
    /// <summary>
    /// Class BoxSet.
    /// </summary>
    public class BoxSet : Folder, IHasTrailers, IHasDisplayOrder, IHasLookupInfo<BoxSetInfo>
    {
        public BoxSet()
        {
            DisplayOrder = "PremiereDate";
        }

        [JsonIgnore]
        protected override bool FilterLinkedChildrenPerUser => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override bool SupportsPeople => true;

        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<BaseItem> LocalTrailers => GetExtras([Tesserafin.Model.Entities.ExtraType.Trailer]).ToArray();

        /// <summary>
        /// Gets or sets the display order.
        /// </summary>
        /// <value>The display order.</value>
        public string DisplayOrder { get; set; }

        [JsonIgnore]
        private bool IsLegacyBoxSet
        {
            get
            {
                if (string.IsNullOrEmpty(Path))
                {
                    return false;
                }

                if (LinkedChildren.Length > 0)
                {
                    return false;
                }

                return !FileSystem.ContainsSubPath(ConfigurationManager.ApplicationPaths.DataPath, Path);
            }
        }

        [JsonIgnore]
        public override bool IsPreSorted => true;

        public override bool SupportsRawQueryItems => false;

        public Guid[] LibraryFolderIds { get; set; }

        protected override bool GetBlockUnratedValue(User user)
        {
            return user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(UnratedItem.Movie);
        }

        public override double GetDefaultPrimaryImageAspectRatio()
            => 2.0 / 3;

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Movie;
        }

        protected override IEnumerable<BaseItem> GetNonCachedChildren(IDirectoryService directoryService)
        {
            if (IsLegacyBoxSet)
            {
                return base.GetNonCachedChildren(directoryService);
            }

            return Enumerable.Empty<BaseItem>();
        }

        protected override IReadOnlyList<BaseItem> LoadChildren()
        {
            if (IsLegacyBoxSet)
            {
                return base.LoadChildren();
            }

            // Save a trip to the database
            return [];
        }

        public override bool IsAuthorizedToDelete(User user, List<Folder> allCollectionFolders)
        {
            return user.HasPermission(PermissionKind.IsAdministrator) || user.HasPermission(PermissionKind.EnableCollectionManagement);
        }

        public override bool IsSaveLocalMetadataEnabled()
        {
            return true;
        }

        private IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User user)
        {
            if (!Enum.TryParse<ItemSortBy>(DisplayOrder, out var sortBy))
            {
                sortBy = ItemSortBy.PremiereDate;
            }

            if (sortBy == ItemSortBy.Default)
            {
                return items;
            }

#pragma warning disable CS0618 // static LibraryManager.Sort facade left in place pending Folder.GetChildren cascade, see docs/major-rewrite-plan-v13.md § PR49/N
            return LibraryManager.Sort(items, user, new[] { sortBy }, SortOrder.Ascending);
#pragma warning restore CS0618
        }

        private IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User user, IItemSortService itemSortService)
        {
            if (!Enum.TryParse<ItemSortBy>(DisplayOrder, out var sortBy))
            {
                sortBy = ItemSortBy.PremiereDate;
            }

            if (sortBy == ItemSortBy.Default)
            {
                return items;
            }

            return itemSortService.Sort(items, user, new[] { sortBy }, SortOrder.Ascending);
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query)
        {
            var children = base.GetChildren(user, includeLinkedChildren, query);
            return Sort(children, user).ToArray();
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query, IItemSortService itemSortService)
        {
            var children = base.GetChildren(user, includeLinkedChildren, out _, query);
            return Sort(children, user, itemSortService).ToArray();
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, out int totalItemCount, InternalItemsQuery query = null)
        {
            var children = base.GetChildren(user, includeLinkedChildren, out totalItemCount, query);
            return Sort(children, user).ToArray();
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, out int totalItemCount, InternalItemsQuery query, IItemSortService itemSortService)
        {
            var children = base.GetChildren(user, includeLinkedChildren, out totalItemCount, query);
            return Sort(children, user, itemSortService).ToArray();
        }

        public override IReadOnlyList<BaseItem> GetRecursiveChildren(User user, InternalItemsQuery query, out int totalCount)
        {
            var children = base.GetRecursiveChildren(user, query, out totalCount);
            return Sort(children, user).ToArray();
        }

        public override IReadOnlyList<BaseItem> GetRecursiveChildren(User user, InternalItemsQuery query, out int totalCount, IItemSortService itemSortService)
        {
            var children = base.GetRecursiveChildren(user, query, out totalCount);
            return Sort(children, user, itemSortService).ToArray();
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager, IItemSortService itemSortService)
        {
            if (SourceType == SourceType.Channel || query.Recursive)
            {
                return base.GetItemsInternal(query, channelManager, collectionManager, userViewManager, tvSeriesManager, itemSortService);
            }

            var rawItems = GetRawQueryItems(
                query,
                (user, includeLinkedChildren, childQuery) => GetChildren(user, includeLinkedChildren, childQuery, itemSortService));

            return PostFilterAndSort(rawItems, query, collectionManager, itemSortService);
        }

        public BoxSetInfo GetLookupInfo()
        {
            return GetItemLookupInfo<BoxSetInfo>();
        }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false)
        {
            if (IsLegacyBoxSet)
            {
                return base.IsVisible(user, skipAllowedTagsCheck);
            }

            if (!IsParentalAllowed(user, skipAllowedTagsCheck))
            {
                return false;
            }

            if (LinkedChildren.Length == 0)
            {
                return true;
            }

            var userLibraryFolderIds = GetLibraryFolderIds(user);
            var libraryFolderIds = LibraryFolderIds ?? GetLibraryFolderIds();

            if (libraryFolderIds.Length == 0)
            {
                return true;
            }

            if (!userLibraryFolderIds.Any(i => libraryFolderIds.Contains(i)))
            {
                return false;
            }

            // If user has parental controls, hide the BoxSet when all children are restricted
            if (user.MaxParentalRatingScore.HasValue)
            {
                var linkedItems = GetLinkedChildren();
                if (linkedItems.Count > 0 && linkedItems.All(child => !child.IsParentalAllowed(user, true)))
                {
                    return false;
                }
            }

            return true;
        }

        public override void MarkPlayed(User user, DateTime? datePlayed, bool resetPosition)
        {
            if (IsLegacyBoxSet)
            {
                base.MarkPlayed(user, datePlayed, resetPosition);
                return;
            }

            foreach (var item in GetLinkedChildren(user))
            {
                item.MarkPlayed(user, datePlayed, resetPosition);
            }
        }

        public override void MarkUnplayed(User user)
        {
            if (IsLegacyBoxSet)
            {
                base.MarkUnplayed(user);
                return;
            }

            foreach (var item in GetLinkedChildren(user))
            {
                item.MarkUnplayed(user);
            }
        }

        public override bool IsVisibleStandalone(User user)
        {
            if (IsLegacyBoxSet)
            {
                return base.IsVisibleStandalone(user);
            }

            return IsVisible(user);
        }

        private Guid[] GetLibraryFolderIds(User user)
        {
            return LibraryManager.GetUserRootFolder().GetChildren(user, true)
                .Select(i => i.Id)
                .ToArray();
        }

        public Guid[] GetLibraryFolderIds()
        {
            var expandedFolders = new List<Guid>();

            return FlattenItems(this, expandedFolders)
                .SelectMany(LibraryManager.GetCollectionFolders)
                .Select(i => i.Id)
                .Distinct()
                .ToArray();
        }

        private IEnumerable<BaseItem> FlattenItems(IEnumerable<BaseItem> items, List<Guid> expandedFolders)
        {
            return items
                .SelectMany(i => FlattenItems(i, expandedFolders));
        }

        private IEnumerable<BaseItem> FlattenItems(BaseItem item, List<Guid> expandedFolders)
        {
            if (item is BoxSet boxset)
            {
                if (!expandedFolders.Contains(item.Id))
                {
                    expandedFolders.Add(item.Id);

                    return FlattenItems(boxset.GetLinkedChildren(), expandedFolders);
                }

                return Array.Empty<BaseItem>();
            }

            return new[] { item };
        }
    }
}
