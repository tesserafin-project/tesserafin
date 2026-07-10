using System;
using Reefin.Controller.Entities;
using Reefin.Database.Implementations.Entities;

namespace Reefin.Controller.Library
{
    /// <summary>
    /// Narrow, read-only boundary for looking up items by id. Implementations are expected to be
    /// cache-aware (repeated lookups for the same id should not necessarily hit the underlying
    /// storage) and user-aware (the overloads that accept a <see cref="User"/> apply that user's
    /// visibility rules to the result).
    /// </summary>
    /// <remarks>
    /// This interface intentionally excludes mutation (create/register/delete), querying/listing,
    /// collection folder access, and path-based lookups - those remain the responsibility of
    /// <see cref="ILibraryManager"/>. The <c>GetItemById&lt;T&gt;(Guid, Guid)</c> overload that
    /// resolves a user id is also excluded; callers needing that convenience should depend on
    /// <see cref="ILibraryManager"/> directly.
    /// </remarks>
    public interface IItemLookupService
    {
        /// <summary>
        /// Gets the item by id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>BaseItem.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="id"/> is <c>null</c>.</exception>
        BaseItem? GetItemById(Guid id);

        /// <summary>
        /// Gets the item by id, as T.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <typeparam name="T">The type of item.</typeparam>
        /// <returns>The item.</returns>
        T? GetItemById<T>(Guid id)
            where T : BaseItem;

        /// <summary>
        /// Gets the item by id, as T, and validates user access.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="user">The user to validate against.</param>
        /// <typeparam name="T">The type of item.</typeparam>
        /// <returns>The item if found.</returns>
        T? GetItemById<T>(Guid id, User? user)
            where T : BaseItem;
    }
}
