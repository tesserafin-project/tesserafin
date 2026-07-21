using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.Plugins.MusicBrainz;

/// <summary>
/// External album artist URLs for MusicBrainz.
/// </summary>
public class MusicBrainzAlbumArtistExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "MusicBrainz Album Artist";

    /// <inheritdoc/>
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is MusicAlbum)
        {
            if (item.TryGetProviderId(MetadataProvider.MusicBrainzAlbumArtist, out var externalId))
            {
                yield return Plugin.Instance!.Configuration.Server + $"/artist/{externalId}";
            }
        }
    }
}
