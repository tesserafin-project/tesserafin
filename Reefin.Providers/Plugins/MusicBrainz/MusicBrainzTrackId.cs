using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.MusicBrainz;

/// <summary>
/// MusicBrainz track id.
/// </summary>
public class MusicBrainzTrackId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MusicBrainz";

    /// <inheritdoc />
    public string Key => MetadataProvider.MusicBrainzTrack.ToString();

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Track;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Audio;
}
