#nullable disable

using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Owns the dependencies (<see cref="Tesserafin.Controller.Channels.IChannelManager"/>,
    /// <see cref="Tesserafin.Controller.Collections.ICollectionManager"/>, <see cref="IUserViewManager"/>,
    /// <see cref="Tesserafin.Controller.TV.ITVSeriesManager"/>) that <c>Folder.GetItems</c> and
    /// <c>Folder.GetItemList</c> currently require every caller to thread through by hand.
    /// First step of moving item-querying logic out of <see cref="BaseItem"/>/<see cref="Folder"/>
    /// and into a dedicated application service, per the point 4 trajectory correction (major rewrite
    /// plan, "Revue externe post-PR14").
    /// </summary>
    public interface IItemQueryService
    {
        /// <summary>
        /// Gets the child items of <paramref name="folder"/> matching <paramref name="query"/>.
        /// </summary>
        /// <param name="folder">Folder to query.</param>
        /// <param name="query">Query to use.</param>
        /// <returns>Query result.</returns>
        QueryResult<BaseItem> GetItems(Folder folder, InternalItemsQuery query);

        /// <summary>
        /// Gets the child items of <paramref name="folder"/> matching <paramref name="query"/>, as a flat list.
        /// </summary>
        /// <param name="folder">Folder to query.</param>
        /// <param name="query">Query to use.</param>
        /// <returns>Matching items.</returns>
        IReadOnlyList<BaseItem> GetItemList(Folder folder, InternalItemsQuery query);

        /// <summary>
        /// Gets items matching <paramref name="query"/>, with no folder scoping applied by the
        /// caller. This is the global (non-folder-scoped) counterpart to
        /// <see cref="GetItems(Folder, InternalItemsQuery)"/> - the overload is selected by the
        /// first argument being an <see cref="InternalItemsQuery"/> rather than a <see cref="Folder"/>.
        /// </summary>
        /// <remarks>
        /// This method owns the read-only orchestration around the query: resolving
        /// <see cref="InternalItemsQuery.ParentId"/> to its item and scoping the query to it when
        /// recursive, scoping the query to the requesting user's visible views via
        /// <see cref="IItemQueryScopeService"/>, honoring <see cref="InternalItemsQuery.EnableTotalRecordCount"/>,
        /// and dispatching to the item repository. Mutation, creation, deletion, and library
        /// scanning are explicitly NOT part of this surface - those remain the responsibility of
        /// <see cref="ILibraryManager"/>.
        /// <para>
        /// <see cref="ILibraryManager.GetItemList(InternalItemsQuery)"/> and
        /// <see cref="ILibraryManager.GetItemsResult(InternalItemsQuery)"/> remain as compatibility
        /// surfaces for now: they cannot delegate to this service yet, since doing so would
        /// re-form the DI cycle via <see cref="IUserViewManager"/> that PR85b's extraction of
        /// <see cref="IItemQueryScopeService"/> exists to avoid. Consumers migrate directly to this
        /// service in PR87.
        /// </para>
        /// </remarks>
        /// <param name="query">Query to use.</param>
        /// <returns>Query result.</returns>
        QueryResult<BaseItem> GetItems(InternalItemsQuery query);

        /// <summary>
        /// Gets items matching <paramref name="query"/>, as a flat list, with no folder scoping
        /// applied by the caller. This is the global (non-folder-scoped) counterpart to
        /// <see cref="GetItemList(Folder, InternalItemsQuery)"/> - the overload is selected by the
        /// first argument being an <see cref="InternalItemsQuery"/> rather than a <see cref="Folder"/>.
        /// </summary>
        /// <remarks>
        /// Same read-only orchestration as <see cref="GetItems(InternalItemsQuery)"/> (parent
        /// resolution, user scoping via <see cref="IItemQueryScopeService"/>, repository dispatch),
        /// minus the total record count. Mutation/create/delete/scan are explicitly NOT part of this
        /// surface - still <see cref="ILibraryManager"/>.
        /// <para>
        /// <see cref="ILibraryManager.GetItemList(InternalItemsQuery)"/> remains a compatibility
        /// surface for now; it cannot delegate here yet (DI cycle via <see cref="IUserViewManager"/>).
        /// Consumers migrate directly to this service in PR87.
        /// </para>
        /// </remarks>
        /// <param name="query">Query to use.</param>
        /// <returns>Matching items.</returns>
        IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query);
    }
}
