using System.Collections.Generic;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Narrow port answering one question: which top-level libraries does an item belong to?
    /// Library membership drives folder-scoped visibility, deletion rights, library options and
    /// inherited metadata language/country.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="ILibraryManager"/> (DEBT-SERVER-2), which keeps its two
    /// <c>GetCollectionFolders</c> members as a delegating facade for existing callers, including
    /// the entities' static <c>BaseItem.LibraryManager</c>. Implementations must not depend on
    /// <see cref="ILibraryManager"/>, directly or deferred: it sits above this port in the
    /// construction graph.
    /// </remarks>
    public interface ICollectionFolderResolver
    {
        /// <summary>
        /// Gets the libraries among the user root folder's children that contain <paramref name="item"/>.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The matching libraries, in root order; empty when none matches.</returns>
        List<Folder> GetCollectionFolders(BaseItem item);

        /// <summary>
        /// Gets the libraries among <paramref name="allUserRootChildren"/> that contain <paramref name="item"/>.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="allUserRootChildren">The libraries to consider.</param>
        /// <returns>The matching libraries, in the given order; empty when none matches.</returns>
        List<Folder> GetCollectionFolders(BaseItem item, IEnumerable<Folder> allUserRootChildren);
    }
}
