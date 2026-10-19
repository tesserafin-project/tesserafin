using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Database.Implementations.Entities;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="ITagVisibilityService"/>, built on the id lookup
    /// (parents) and the library membership resolver only.
    /// </summary>
    /// <remarks>
    /// The combination and the filter are the entities' own (<see cref="BaseItem.CombineInheritedTags"/>,
    /// <see cref="BaseItem.EvaluateTagVisibility"/>); only where the parents and libraries come from
    /// differs from <see cref="BaseItem.GetInheritedTags()"/> (DEBT-SERVER-3).
    /// </remarks>
    internal sealed class TagVisibilityService : ITagVisibilityService
    {
        private readonly IItemLookupService _itemLookupService;
        private readonly ICollectionFolderResolver _collectionFolderResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="TagVisibilityService"/> class.
        /// </summary>
        /// <param name="itemLookupService">The cached id lookup (parents).</param>
        /// <param name="collectionFolderResolver">The library membership resolver.</param>
        public TagVisibilityService(IItemLookupService itemLookupService, ICollectionFolderResolver collectionFolderResolver)
        {
            _itemLookupService = itemLookupService;
            _collectionFolderResolver = collectionFolderResolver;
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
