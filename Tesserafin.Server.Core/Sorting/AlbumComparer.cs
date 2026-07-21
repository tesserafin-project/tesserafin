using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Sorting
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
