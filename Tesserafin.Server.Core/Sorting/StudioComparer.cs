#pragma warning disable CS1591

using System;
using System.Globalization;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;

namespace Tesserafin.Server.Core.Sorting
{
    public class StudioComparer : IBaseItemComparer
    {
        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public ItemSortBy Type => ItemSortBy.Studio;

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

            return CultureInfo.InvariantCulture.CompareInfo.Compare(x.Studios.FirstOrDefault(), y.Studios.FirstOrDefault(), CompareOptions.NumericOrdering);
        }
    }
}
