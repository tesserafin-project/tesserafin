#pragma warning disable CS1591

using Reefin.Controller.Entities;
using Reefin.Controller.Sorting;
using Reefin.Data.Enums;
using Reefin.Model.Querying;

namespace Reefin.Server.Core.Sorting
{
    public class IsFolderComparer : IBaseItemComparer
    {
        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public ItemSortBy Type => ItemSortBy.IsFolder;

        /// <summary>
        /// Compares the specified x.
        /// </summary>
        /// <param name="x">The x.</param>
        /// <param name="y">The y.</param>
        /// <returns>System.Int32.</returns>
        public int Compare(BaseItem? x, BaseItem? y)
        {
            return GetValue(x).CompareTo(GetValue(y));
        }

        /// <summary>
        /// Gets the value.
        /// </summary>
        /// <param name="x">The x.</param>
        /// <returns>System.String.</returns>
        private static int GetValue(BaseItem? x)
        {
            return x?.IsFolder ?? true ? 0 : 1;
        }
    }
}
