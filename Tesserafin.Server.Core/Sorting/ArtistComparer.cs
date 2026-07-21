using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Sorting
{
    /// <summary>
    /// Class ArtistComparer.
    /// </summary>
    public class ArtistComparer : IBaseItemComparer
    {
        /// <inheritdoc />
        public ItemSortBy Type => ItemSortBy.Artist;

        /// <inheritdoc />
        public int Compare(BaseItem? x, BaseItem? y)
        {
            return string.Compare(GetValue(x), GetValue(y), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the value.
        /// </summary>
        /// <param name="x">The x.</param>
        /// <returns>System.String.</returns>
        private static string? GetValue(BaseItem? x)
        {
            if (x is not Audio audio)
            {
                return string.Empty;
            }

            return audio.Artists.Count == 0 ? null : audio.Artists[0];
        }
    }
}
