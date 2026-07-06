using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Books.Isbn
{
    /// <inheritdoc />
    public class IsbnExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "ISBN";

        /// <inheritdoc />
        public string Key => "ISBN";

        /// <inheritdoc />
        public ExternalIdMediaType? Type => null;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Book;
    }
}
