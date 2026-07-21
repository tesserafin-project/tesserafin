using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.Plugins.MusicBrainz;

/// <summary>
/// External album URLs for MusicBrainz.
/// </summary>
public class MusicBrainzAlbumExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "MusicBrainz Album";

    /// <inheritdoc/>
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is MusicAlbum)
        {
            if (item.TryGetProviderId(MetadataProvider.MusicBrainzAlbum, out var externalId))
            {
                yield return Plugin.Instance!.Configuration.Server + $"/release/{externalId}";
            }
        }
    }
}
