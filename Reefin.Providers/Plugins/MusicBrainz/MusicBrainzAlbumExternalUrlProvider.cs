using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;

namespace Reefin.Providers.Plugins.MusicBrainz;

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
