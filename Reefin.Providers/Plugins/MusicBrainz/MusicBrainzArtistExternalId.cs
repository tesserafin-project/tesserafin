using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.MusicBrainz;

/// <summary>
/// MusicBrainz artist external id.
/// </summary>
public class MusicBrainzArtistExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MusicBrainz";

    /// <inheritdoc />
    public string Key => MetadataProvider.MusicBrainzArtist.ToString();

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Artist;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is MusicArtist;
}
