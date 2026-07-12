using System.Collections.Generic;
using Reefin.Controller.Entities;

namespace Reefin.Controller.Library
{
    /// <summary>
    /// Narrow, read-only boundary for resolving an item's place in the library hierarchy
    /// (parent, owner, and ancestor chain). Composes <see cref="IItemLookupService"/> so that
    /// callers do not need to thread a lookup service through every signature, nor fall back to
    /// the static <see cref="BaseItem.LibraryManager"/>.
    /// </summary>
    public interface IItemHierarchyService
    {
        /// <summary>
        /// Gets the immediate parent of <paramref name="item"/>, resolved through the injected
        /// lookup.
        /// </summary>
        /// <param name="item">The item whose parent is being resolved.</param>
        /// <returns>The parent, or <c>null</c> if <paramref name="item"/> has no parent.</returns>
        BaseItem? GetParent(BaseItem item);

        /// <summary>
        /// Gets the owner of <paramref name="item"/>, resolved through the injected lookup.
        /// </summary>
        /// <param name="item">The item whose owner is being resolved.</param>
        /// <returns>The owner, or <c>null</c> if <paramref name="item"/> has no owner.</returns>
        BaseItem? GetOwner(BaseItem item);

        /// <summary>
        /// Walks the ancestor chain of <paramref name="item"/>, from its immediate parent upward,
        /// resolving each hop through the injected lookup.
        /// </summary>
        /// <param name="item">The item whose ancestors are being resolved.</param>
        /// <returns>
        /// The ancestors of <paramref name="item"/>, starting with its immediate parent and
        /// proceeding upward to the root.
        /// </returns>
        IEnumerable<BaseItem> GetAncestors(BaseItem item);

        /// <summary>
        /// Finds the nearest ancestor of <paramref name="item"/> assignable to <typeparamref name="T"/>,
        /// walking the ancestor chain from the immediate parent upward through the injected lookup.
        /// </summary>
        /// <param name="item">The item whose ancestors are being searched.</param>
        /// <typeparam name="T">The ancestor type to look for.</typeparam>
        /// <returns>
        /// The nearest ancestor assignable to <typeparamref name="T"/>, or <c>null</c> if none is found.
        /// </returns>
        T? FindAncestor<T>(BaseItem item)
            where T : Folder;
    }
}
