using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;

namespace Reefin.Providers.Plugins.MusicBrainz;

/// <summary>
/// External track URLs for MusicBrainz.
/// </summary>
public class MusicBrainzTrackExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc/>
    public string Name => "MusicBrainz Track";

    /// <inheritdoc/>
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Audio)
        {
            if (item.TryGetProviderId(MetadataProvider.MusicBrainzTrack, out var externalId))
            {
                yield return Plugin.Instance!.Configuration.Server + $"/track/{externalId}";
            }
        }
    }
}
