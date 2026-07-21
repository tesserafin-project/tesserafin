using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Plugins.Tmdb.TV
{
    /// <summary>
    /// External id for a TMDb season.
    /// </summary>
    public class TmdbSeasonExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => TmdbUtils.ProviderName;

        /// <inheritdoc />
        public string Key => MetadataProvider.Tmdb.ToString();

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Season;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Season;
    }
}
