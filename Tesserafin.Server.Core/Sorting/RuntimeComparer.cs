using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Sorting
{
    /// <summary>
    /// Class RuntimeComparer.
    /// </summary>
    public class RuntimeComparer : IBaseItemComparer
    {
        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public ItemSortBy Type => ItemSortBy.Runtime;

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

            return (x.RunTimeTicks ?? 0).CompareTo(y.RunTimeTicks ?? 0);
        }
    }
}
