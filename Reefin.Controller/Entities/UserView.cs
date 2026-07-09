#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Library;
using Reefin.Controller.Providers;
using Reefin.Controller.TV;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Entities;
using Reefin.Extensions;
using Reefin.Model.Querying;

namespace Reefin.Controller.Entities
{
    public class UserView : Folder, IHasCollectionType
    {
        private static readonly CollectionType?[] _viewTypesEligibleForGrouping =
        {
            Reefin.Data.Enums.CollectionType.movies,
            Reefin.Data.Enums.CollectionType.tvshows,
            null
        };

        private static readonly CollectionType?[] _originalFolderViewTypes =
        {
            Reefin.Data.Enums.CollectionType.books,
            Reefin.Data.Enums.CollectionType.musicvideos,
            Reefin.Data.Enums.CollectionType.homevideos,
            Reefin.Data.Enums.CollectionType.photos,
            Reefin.Data.Enums.CollectionType.music,
            Reefin.Data.Enums.CollectionType.boxsets
        };

        public static ITVSeriesManager TVSeriesManager { get; set; }

        /// <summary>
        /// Gets or sets the view type.
        /// </summary>
        public CollectionType? ViewType { get; set; }

        /// <summary>
        /// Gets or sets the display parent id.
        /// </summary>
        public new Guid DisplayParentId { get; set; }

        /// <summary>
        /// Gets or sets the user id.
        /// </summary>
        public Guid? UserId { get; set; }

        /// <inheritdoc />
        [JsonIgnore]
        public CollectionType? CollectionType => ViewType;

        /// <inheritdoc />
        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        /// <inheritdoc />
        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        /// <inheritdoc />
        [JsonIgnore]
        public override bool SupportsPeople => false;

        /// <inheritdoc />
        public override bool SupportsRawQueryItems => false;

        /// <inheritdoc />
        public override IEnumerable<Guid> GetIdsForAncestorQuery()
        {
            if (!DisplayParentId.IsEmpty())
            {
                yield return DisplayParentId;
            }
            else if (!ParentId.IsEmpty())
            {
                yield return ParentId;
            }
            else
            {
                yield return Id;
            }
        }

        /// <inheritdoc />
        public override int GetChildCount(User user)
        {
            return GetChildren(user, true, null).Count;
        }

        /// <inheritdoc />
        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            var parent = this as Folder;

            if (!DisplayParentId.IsEmpty())
            {
                parent = LibraryManager.GetItemById(DisplayParentId) as Folder ?? parent;
            }
            else if (!ParentId.IsEmpty())
            {
                parent = LibraryManager.GetItemById(ParentId) as Folder ?? parent;
            }

            return new UserViewBuilder(userViewManager, LibraryManager, Logger, UserDataManager, tvSeriesManager, channelManager, collectionManager)
                .GetUserItems(parent, this, CollectionType, query);
        }

        /// <inheritdoc />
        public override List<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query)
        {
            query ??= new InternalItemsQuery(user);

            query.EnableTotalRecordCount = false;

            // GetChildren isn't part of the GetItemsInternal/GetItems/GetItemList chain the
            // channelManager parameter was threaded through (out of scope: GetChildren is a much
            // wider virtual with many more overrides/callers) — falls back to the statics here.
            // Documented legitimate caller of the obsolete 5-parameter overload — see
            // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
            var result = GetItemList(query, ChannelManager, CollectionManager, UserViewManager, TVSeriesManager);
#pragma warning restore CS0618

            return result.ToList();
        }

        /// <inheritdoc />
        public override bool CanDelete()
        {
            return false;
        }

        /// <inheritdoc />
        public override bool IsSaveLocalMetadataEnabled()
        {
            return true;
        }

        /// <inheritdoc />
        public override IReadOnlyList<BaseItem> GetRecursiveChildren(User user, InternalItemsQuery query, out int totalCount)
        {
            query.SetUser(user);
            query.Recursive = true;
            query.EnableTotalRecordCount = false;
            query.ForceDirect = true;

            // Same scope boundary as GetChildren above: GetRecursiveChildren isn't part of the
            // threaded chain, falls back to the statics.
            // Documented legitimate caller of the obsolete 5-parameter overload — see
            // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
            var data = GetItemList(query, ChannelManager, CollectionManager, UserViewManager, TVSeriesManager);
#pragma warning restore CS0618
            totalCount = data.Count;

            return data;
        }

        /// <inheritdoc />
        protected override IReadOnlyList<BaseItem> GetEligibleChildrenForRecursiveChildren(User user)
        {
            return GetChildren(user, false, null);
        }

        public static bool IsUserSpecific(Folder folder)
        {
            if (folder is not ICollectionFolder collectionFolder)
            {
                return false;
            }

            if (folder is ISupportsUserSpecificView supportsUserSpecific
                && supportsUserSpecific.EnableUserSpecificView)
            {
                return true;
            }

            return collectionFolder.CollectionType == Reefin.Data.Enums.CollectionType.playlists;
        }

        public static bool IsEligibleForGrouping(Folder folder)
        {
            return folder is ICollectionFolder collectionFolder
                    && IsEligibleForGrouping(collectionFolder.CollectionType);
        }

        public static bool IsEligibleForGrouping(CollectionType? viewType)
        {
            return _viewTypesEligibleForGrouping.Contains(viewType);
        }

        public static bool EnableOriginalFolder(CollectionType? viewType)
        {
            return _originalFolderViewTypes.Contains(viewType);
        }

        protected override Task ValidateChildrenInternal(IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
