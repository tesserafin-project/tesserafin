using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.Tmdb.TV
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
