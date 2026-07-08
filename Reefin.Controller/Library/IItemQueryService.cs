#nullable disable

using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Model.Querying;

namespace Reefin.Controller.Library
{
    /// <summary>
    /// Owns the dependencies (<see cref="Reefin.Controller.Channels.IChannelManager"/>,
    /// <see cref="Reefin.Controller.Collections.ICollectionManager"/>, <see cref="IUserViewManager"/>,
    /// <see cref="Reefin.Controller.TV.ITVSeriesManager"/>) that <see cref="Folder.GetItems"/> and
    /// <see cref="Folder.GetItemList"/> currently require every caller to thread through by hand.
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
    }
}
