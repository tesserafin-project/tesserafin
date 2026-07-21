#pragma warning disable CS1591

using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Database.Implementations.Entities;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Applies user visibility rules on top of <see cref="IItemLookupService"/>. This is the sole
    /// production implementation of <see cref="IItemAccessService"/>.
    /// </summary>
    /// <remarks>
    /// Extracted in PR77 from the user-aware overload previously living on
    /// <c>ItemLookupService.GetItemById&lt;T&gt;(Guid, User?)</c>/<c>ItemIsVisible</c> - lookup
    /// (finding an item) and access control (deciding whether a user may see it) are different
    /// responsibilities, and the latter was leaking a hidden static dependency into the former.
    /// That residual static dependency - <see cref="BaseItem.LibraryManager"/>, reached indirectly
    /// through <see cref="BaseItem.IsVisibleStandalone(User)"/> (collection folder / channel
    /// visibility) and <see cref="BaseItem.GetInheritedTags"/> (tag inheritance through parents) -
    /// is now explicitly and exclusively localized here, at the visibility boundary, instead of
    /// being an implicit dependency of the plain item lookup cache. <see cref="ItemLookupService"/>
    /// itself has no knowledge of users or visibility any more.
    /// </remarks>
    internal sealed class ItemAccessService : IItemAccessService
    {
        private readonly IItemLookupService _itemLookupService;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemAccessService"/> class.
        /// </summary>
        /// <param name="itemLookupService">The item lookup service used to resolve the item before applying visibility.</param>
        public ItemAccessService(IItemLookupService itemLookupService)
        {
            _itemLookupService = itemLookupService;
        }

        /// <inheritdoc />
        public T? GetVisibleItemById<T>(Guid id, User user)
            where T : BaseItem
        {
            var item = _itemLookupService.GetItemById<T>(id);
            return ItemIsVisible(item, user) ? item : null;
        }

        /// <summary>
        /// Determines whether <paramref name="item"/> is visible to <paramref name="user"/>.
        /// <see cref="UserRootFolder"/> is always visible regardless of a user's tag/parental
        /// restrictions.
        /// </summary>
        private static bool ItemIsVisible(BaseItem? item, User user)
        {
            if (item is null)
            {
                return false;
            }

            return item is UserRootFolder || item.IsVisibleStandalone(user);
        }
    }
}
