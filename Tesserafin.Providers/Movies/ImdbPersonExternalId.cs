#pragma warning disable CS1591

using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Movies
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
