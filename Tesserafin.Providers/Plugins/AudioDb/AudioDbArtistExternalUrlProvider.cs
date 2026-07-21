using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.Plugins.AudioDb;

/// <summary>
/// External artist URLs for AudioDb.
/// </summary>
public class AudioDbArtistExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "TheAudioDb Artist";

    /// <inheritdoc/>
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId(MetadataProvider.AudioDbArtist, out var externalId))
        {
            var baseUrl = "https://www.theaudiodb.com/";
            switch (item)
            {
                case MusicArtist:
                case Person:
                    yield return baseUrl + $"artist/{externalId}";
                    break;
            }
        }
    }
}
