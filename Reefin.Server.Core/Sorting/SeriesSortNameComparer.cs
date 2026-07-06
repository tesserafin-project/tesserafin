#pragma warning disable CS1591

using System;
using Reefin.Controller.Entities;
using Reefin.Controller.Sorting;
using Reefin.Data.Enums;
using Reefin.Model.Querying;

namespace Reefin.Server.Core.Sorting
{
    public class SeriesSortNameComparer : IBaseItemComparer
    {
        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public ItemSortBy Type => ItemSortBy.SeriesSortName;

        /// <summary>
        /// Compares the specified x.
        /// </summary>
        /// <param name="x">The x.</param>
        /// <param name="y">The y.</param>
        /// <returns>System.Int32.</returns>
        public int Compare(BaseItem? x, BaseItem? y)
        {
            return string.Compare(GetValue(x), GetValue(y), StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetValue(BaseItem? item)
        {
            var hasSeries = item as IHasSeries;
            return hasSeries?.FindSeriesSortName();
        }
    }
}
