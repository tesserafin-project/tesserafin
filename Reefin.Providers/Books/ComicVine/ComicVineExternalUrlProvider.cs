using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;

namespace Reefin.Providers.Books.ComicVine;

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
