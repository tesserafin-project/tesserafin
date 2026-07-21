using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.Plugins.MusicBrainz;

/// <summary>
/// External release group URLs for MusicBrainz.
/// </summary>
public class MusicBrainzReleaseGroupExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "MusicBrainz Release Group";

    /// <inheritdoc/>
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is MusicAlbum)
        {
            if (item.TryGetProviderId(MetadataProvider.MusicBrainzReleaseGroup, out var externalId))
            {
                yield return Plugin.Instance!.Configuration.Server + $"/release-group/{externalId}";
            }
        }
    }
}
