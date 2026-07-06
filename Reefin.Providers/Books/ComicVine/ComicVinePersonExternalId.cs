using Reefin.Controller.Entities;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Books.ComicVine
{
    /// <inheritdoc />
    public class ComicVinePersonExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "Comic Vine";

        /// <inheritdoc />
        public string Key => "ComicVine";

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Person;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Person;
    }
}
