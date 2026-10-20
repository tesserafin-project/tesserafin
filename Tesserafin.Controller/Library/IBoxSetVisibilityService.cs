using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// The items a box set's visibility reads, over explicit dependencies, on top of
    /// <see cref="IStandaloneVisibilityService"/>.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="BoxSet"/> (DEBT-SERVER-5): a box set that is not legacy resolves the
    /// libraries and its linked children through the static <c>BaseItem.LibraryManager</c>. Passed as the
    /// <see cref="ITagVisibilityService"/> of its visibility check, it supplies them instead, while the
    /// rules stay the box set's own. Implementations must not depend on <see cref="ILibraryManager"/>,
    /// directly or deferred.
    /// </remarks>
    public interface IBoxSetVisibilityService : IStandaloneVisibilityService
    {
        /// <summary>
        /// Gets an item by id: a library, or a box set's linked child.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <returns>The item; <c>null</c> when none has that id.</returns>
        BaseItem? GetItemById(Guid id);
    }
}
