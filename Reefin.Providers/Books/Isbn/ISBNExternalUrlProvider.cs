using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;

namespace Reefin.Providers.Books.Isbn;

/// <inheritdoc/>
public class IsbnExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "ISBN";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId("ISBN", out var externalId))
        {
            if (item is Book)
            {
                yield return $"https://search.worldcat.org/search?q=bn:{externalId}";
            }
        }
    }
}
