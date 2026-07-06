using System;
using Reefin.Controller.Entities;
using Reefin.Controller.Sorting;
using Reefin.Data.Enums;
using Reefin.Model.Querying;

namespace Reefin.Server.Core.Sorting
{
    /// <summary>
    /// Class NameComparer.
    /// </summary>
    public class NameComparer : IBaseItemComparer
    {
        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public ItemSortBy Type => ItemSortBy.Name;

        /// <summary>
        /// Compares the specified x.
        /// </summary>
        /// <param name="x">The x.</param>
        /// <param name="y">The y.</param>
        /// <returns>System.Int32.</returns>
        public int Compare(BaseItem? x, BaseItem? y)
        {
            ArgumentNullException.ThrowIfNull(x);

            ArgumentNullException.ThrowIfNull(y);

            return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
