using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.TV;

/// <summary>
/// External URLs for TMDb.
/// </summary>
public class Zap2ItExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "Zap2It";

    /// <inheritdoc/>
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId(MetadataProvider.Zap2It, out var externalId))
        {
            yield return $"http://tvlistings.zap2it.com/overview.html?programSeriesId={externalId}";
        }
    }
}
