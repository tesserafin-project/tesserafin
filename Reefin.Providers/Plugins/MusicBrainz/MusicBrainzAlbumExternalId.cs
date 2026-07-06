using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.MusicBrainz;

/// <summary>
/// MusicBrainz album external id.
/// </summary>
public class MusicBrainzAlbumExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MusicBrainz";

    /// <inheritdoc />
    public string Key => MetadataProvider.MusicBrainzAlbum.ToString();

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Album;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Audio || item is MusicAlbum;
}
