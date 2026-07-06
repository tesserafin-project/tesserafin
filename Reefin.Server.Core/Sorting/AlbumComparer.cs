using System;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Sorting;
using Reefin.Data.Enums;
using Reefin.Model.Querying;

namespace Reefin.Server.Core.Sorting
{
    /// <summary>
    /// Class AlbumComparer.
    /// </summary>
    public class AlbumComparer : IBaseItemComparer
    {
        /// <summary>
        /// Gets the name.
        /// </summary>
        /// <value>The name.</value>
        public ItemSortBy Type => ItemSortBy.Album;

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

        /// <summary>
        /// Gets the value.
        /// </summary>
        /// <param name="x">The x.</param>
        /// <returns>System.String.</returns>
        private static string GetValue(BaseItem? x)
        {
            return x is Audio audio ? audio.Album : string.Empty;
        }
    }
}
