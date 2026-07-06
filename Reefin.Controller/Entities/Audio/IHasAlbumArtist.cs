#nullable disable

#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using Reefin.Controller.Library;

namespace Reefin.Controller.Entities.Audio
{
    public interface IHasAlbumArtist
    {
        IReadOnlyList<string> AlbumArtists { get; set; }
    }

    public interface IHasArtist
    {
        /// <summary>
        /// Gets or sets the artists.
        /// </summary>
        /// <value>The artists.</value>
        IReadOnlyList<string> Artists { get; set; }
    }

    public static class Extensions
    {
        public static IEnumerable<string> GetAllArtists<T>(this T item)
            where T : IHasArtist, IHasAlbumArtist
        {
            return item.AlbumArtists.Concat(item.Artists).DistinctNames();
        }
    }
}
