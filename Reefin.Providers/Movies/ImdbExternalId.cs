#pragma warning disable CS1591

using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Entities.TV;
using Reefin.Controller.LiveTv;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Movies
{
    public class ImdbExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "IMDb";

        /// <inheritdoc />
        public string Key => MetadataProvider.Imdb.ToString();

        /// <inheritdoc />
        public ExternalIdMediaType? Type => null;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item)
        {
            // Supports images for tv movies
            if (item is LiveTvProgram tvProgram && tvProgram.IsMovie)
            {
                return true;
            }

            return item is Movie || item is MusicVideo || item is Series || item is Episode || item is Trailer;
        }
    }
}
