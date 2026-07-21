using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Narrow port exposing <see cref="ILibraryManager"/>'s root-folder lifecycle
    /// (creation/caching of the per-server <see cref="UserRootFolder"/>) to consumers that need it
    /// without depending on the full <see cref="ILibraryManager"/> surface.
    /// </summary>
    /// <remarks>
    /// Introduced in PR85b for <c>IItemQueryScopeService</c>, whose grouped-folders branch of
    /// top-parent resolution needs the user root folder to enumerate top-level
    /// <c>CollectionFolder</c>s. <c>LibraryManager</c> is the sole implementation - it already owns
    /// the root-folder cache and creation logic - so this port simply carves out that one method
    /// rather than duplicating the lifecycle elsewhere. Actually extracting the root-folder
    /// lifecycle off <c>LibraryManager</c> is a separate, later chantier; until then this port stays
    /// backed by <c>LibraryManager</c>.
    /// </remarks>
    public interface IUserRootFolderProvider
    {
        /// <summary>
        /// Gets the user root folder, creating and caching it on first access.
        /// </summary>
        /// <returns>The user root <see cref="Folder"/>.</returns>
        Folder GetUserRootFolder();
    }
}
