using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.Plugins.MusicBrainz;

/// <summary>
/// External artist URLs for MusicBrainz.
/// </summary>
public class MusicBrainzArtistExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "MusicBrainz Artist";

    /// <inheritdoc/>
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId(MetadataProvider.MusicBrainzArtist, out var externalId))
        {
            switch (item)
            {
                case MusicArtist:
                case Person:
                    yield return Plugin.Instance!.Configuration.Server + $"/artist/{externalId}";

                    break;
            }
        }
    }
}
