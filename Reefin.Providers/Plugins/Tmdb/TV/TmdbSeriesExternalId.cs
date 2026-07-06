using Reefin.Controller.Entities.TV;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.Tmdb.TV
{
    /// <summary>
    /// External id for a TMDb series.
    /// </summary>
    public class TmdbSeriesExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => TmdbUtils.ProviderName;

        /// <inheritdoc />
        public string Key => MetadataProvider.Tmdb.ToString();

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Series;
    }
}
