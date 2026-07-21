using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;

namespace Tesserafin.Providers.Plugins.MusicBrainz;

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
