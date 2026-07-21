using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Books.GoogleBooks
{
    /// <inheritdoc />
    public class GoogleBooksExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "Google Books";

        /// <inheritdoc />
        public string Key => "GoogleBooks";

        /// <inheritdoc />
        public ExternalIdMediaType? Type => null;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Book;
    }
}
