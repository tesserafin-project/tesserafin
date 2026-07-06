using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.MusicBrainz;

/// <summary>
/// MusicBrainz other artist external id.
/// </summary>
public class MusicBrainzOtherArtistExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MusicBrainz";

    /// <inheritdoc />
    public string Key => MetadataProvider.MusicBrainzArtist.ToString();

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.OtherArtist;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Audio or MusicAlbum;
}
