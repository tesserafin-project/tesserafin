using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Books.ComicVine
{
    /// <inheritdoc />
    public class ComicVineExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "Comic Vine";

        /// <inheritdoc />
        public string Key => "ComicVine";

        /// <inheritdoc />
        public ExternalIdMediaType? Type => null;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Book;
    }
}
