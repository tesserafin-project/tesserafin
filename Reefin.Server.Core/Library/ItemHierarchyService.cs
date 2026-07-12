#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;

namespace Reefin.Server.Core.Library
{
    /// <summary>
    /// Resolves an item's place in the library hierarchy (parent, owner, ancestor chain) purely
    /// through <see cref="IItemLookupService"/>. This is the sole production implementation of
    /// <see cref="IItemHierarchyService"/>. It depends only on <see cref="IItemLookupService"/> -
    /// never on <see cref="ILibraryManager"/> - keeping hierarchy resolution static-free, by
    /// delegating to the service-aware <c>BaseItem</c> overloads (<c>GetParent(IItemLookupService)</c>,
    /// <c>GetOwner(IItemLookupService)</c>, <c>GetParents(IItemLookupService)</c>,
    /// <c>FindParent&lt;T&gt;(IItemLookupService)</c>) instead of their static-backed counterparts.
    /// </summary>
    internal sealed class ItemHierarchyService : IItemHierarchyService
    {
        private readonly IItemLookupService _itemLookupService;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemHierarchyService"/> class.
        /// </summary>
        /// <param name="itemLookupService">The item lookup service used to resolve hierarchy hops.</param>
        public ItemHierarchyService(IItemLookupService itemLookupService)
        {
            _itemLookupService = itemLookupService;
        }

        /// <inheritdoc />
        public BaseItem? GetParent(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return item.GetParent(_itemLookupService);
        }

        /// <inheritdoc />
        public BaseItem? GetOwner(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return item.GetOwner(_itemLookupService);
        }

        /// <inheritdoc />
        public IEnumerable<BaseItem> GetAncestors(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            return item.GetParents(_itemLookupService);
        }

        /// <inheritdoc />
        public T? FindAncestor<T>(BaseItem item)
            where T : Folder
        {
            ArgumentNullException.ThrowIfNull(item);
            return item.FindParent<T>(_itemLookupService);
        }
    }
}
