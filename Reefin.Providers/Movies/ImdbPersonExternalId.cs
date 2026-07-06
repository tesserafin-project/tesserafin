#pragma warning disable CS1591

using Reefin.Controller.Entities;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Movies
{
    public class ImdbPersonExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "IMDb";

        /// <inheritdoc />
        public string Key => MetadataProvider.Imdb.ToString();

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Person;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Person;
    }
}
