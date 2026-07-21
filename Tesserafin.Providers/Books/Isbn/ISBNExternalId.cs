using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Books.Isbn
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
