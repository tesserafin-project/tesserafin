using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Books.ComicVine
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
