using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;

namespace Reefin.Providers.Plugins.AudioDb;

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
