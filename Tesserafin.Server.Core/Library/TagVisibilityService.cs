using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Database.Implementations.Entities;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="ITagVisibilityService"/>,
    /// <see cref="IStandaloneVisibilityService"/> and <see cref="IBoxSetVisibilityService"/>, built on the id lookup (parents), the library
    /// membership resolver and the user root folder (libraries) only.
    /// </summary>
    /// <remarks>
    /// The combination and the filter are the entities' own (<see cref="BaseItem.CombineInheritedTags"/>,
    /// <see cref="BaseItem.EvaluateTagVisibility"/>); only where the parents and libraries come from
    /// differs from <see cref="BaseItem.GetInheritedTags()"/> (DEBT-SERVER-3). The same holds for the
    /// parent and library checks of the standalone visibility, which stay in the entities (DEBT-SERVER-4),
    /// and for the box set's library and linked-children checks (DEBT-SERVER-5).
    /// </remarks>
    internal sealed class TagVisibilityService : IBoxSetVisibilityService
    {
        private readonly IItemLookupService _itemLookupService;
        private readonly ICollectionFolderResolver _collectionFolderResolver;
        private readonly IUserRootFolderProvider _userRootFolderProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="TagVisibilityService"/> class.
        /// </summary>
        /// <param name="itemLookupService">The cached id lookup (parents).</param>
        /// <param name="collectionFolderResolver">The library membership resolver.</param>
        /// <param name="userRootFolderProvider">The user root folder (its children are the libraries).</param>
        public TagVisibilityService(IItemLookupService itemLookupService, ICollectionFolderResolver collectionFolderResolver, IUserRootFolderProvider userRootFolderProvider)
        {
            _itemLookupService = itemLookupService;
            _collectionFolderResolver = collectionFolderResolver;
            _userRootFolderProvider = userRootFolderProvider;
        }

        /// <inheritdoc />
        public IReadOnlyList<BaseItem> GetParents(BaseItem item)
        {
            return item.GetParents(_itemLookupService).ToList();
        }

        /// <inheritdoc />
        public BaseItem? GetDisplayParent(BaseItem item)
        {
            var id = item.DisplayParentId;
            return id.Equals(Guid.Empty) ? null : _itemLookupService.GetItemById(id);
        }

        /// <inheritdoc />
        public IReadOnlyList<Folder> GetCollectionFolders(BaseItem item)
        {
            return _collectionFolderResolver.GetCollectionFolders(item);
        }

        /// <inheritdoc />
        public IReadOnlyList<Guid> GetLibraryIds()
        {
            return CollectionFolderResolver.GetLibraries(_userRootFolderProvider.GetUserRootFolder(), _itemLookupService).Select(i => i.Id).ToList();
        }

        /// <inheritdoc />
        public BaseItem? GetItemById(Guid id)
        {
            return _itemLookupService.GetItemById(id);
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetInheritedTags(BaseItem item)
        {
            return BaseItem.CombineInheritedTags(item, item.GetParents(_itemLookupService), _collectionFolderResolver.GetCollectionFolders(item));
        }

        /// <inheritdoc />
        public bool IsVisibleViaTags(BaseItem item, User user, bool skipAllowedTagsCheck)
        {
            return BaseItem.EvaluateTagVisibility(user, skipAllowedTagsCheck, () => GetInheritedTags(item), () => item.GetParent(_itemLookupService) ?? item);
        }
    }
}
