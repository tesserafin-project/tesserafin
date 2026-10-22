using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// The path search a box set's legacy linked children take, on top of <see cref="IBoxSetVisibilityService"/>.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="BoxSet"/> (DEBT-SERVER-7): a linked child without an id, or whose id finds
    /// nothing, is searched by path through the static <c>BaseItem.LibraryManager</c>. Passed as the
    /// <see cref="ITagVisibilityService"/> of its visibility check, it supplies the search instead.
    /// Implementations must not depend on <see cref="ILibraryManager"/>, directly or deferred.
    /// </remarks>
    public interface ILinkedChildVisibilityService : IBoxSetVisibilityService
    {
        /// <summary>
        /// Finds the item at a path, as <see cref="ILibraryManager.FindByPath"/> does with no folder filter.
        /// </summary>
        /// <param name="path">The absolute path.</param>
        /// <returns>The newest item at that path; <c>null</c> when none.</returns>
        BaseItem? FindByPath(string path);
    }
}
