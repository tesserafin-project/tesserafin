using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Data.Enums;

namespace Reefin.Controller.Sorting
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
