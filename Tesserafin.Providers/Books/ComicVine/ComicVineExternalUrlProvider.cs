using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.Books.ComicVine;

/// <inheritdoc/>
public class ComicVineExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "Comic Vine";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId("ComicVine", out var externalId))
        {
            switch (item)
            {
                case Person:
                case Book:
                    yield return $"https://comicvine.gamespot.com/{externalId}";
                    break;
            }
        }
    }
}
