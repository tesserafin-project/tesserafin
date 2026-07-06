using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using Reefin.Data.Enums;

namespace MediaBrowser.Controller.Sorting
{
    /// <summary>
    /// Interface IBaseItemComparer.
    /// </summary>
    public interface IBaseItemComparer : IComparer<BaseItem?>
    {
        /// <summary>
        /// Gets the comparer type.
        /// </summary>
        ItemSortBy Type { get; }
    }
}
