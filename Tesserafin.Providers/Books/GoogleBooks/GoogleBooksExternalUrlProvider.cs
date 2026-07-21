using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.Books.GoogleBooks;

/// <inheritdoc/>
public class GoogleBooksExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "Google Books";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId("GoogleBooks", out var externalId))
        {
            if (item is Book)
            {
                yield return $"https://books.google.com/books?id={externalId}";
            }
        }
    }
}
