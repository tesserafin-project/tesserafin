using Reefin.Controller.Entities.Movies;
using Reefin.Controller.LiveTv;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.Tmdb.Movies
{
    /// <summary>
    /// External id for a TMDb movie.
    /// </summary>
    public class TmdbMovieExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => TmdbUtils.ProviderName;

        /// <inheritdoc />
        public string Key => MetadataProvider.Tmdb.ToString();

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item)
        {
            // Supports images for tv movies
            if (item is LiveTvProgram tvProgram && tvProgram.IsMovie)
            {
                return true;
            }

            return item is Movie;
        }
    }
}
