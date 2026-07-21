using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Plugins.MusicBrainz;

/// <summary>
/// MusicBrainz album artist external id.
/// </summary>
public class MusicBrainzAlbumArtistExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MusicBrainz";

    /// <inheritdoc />
    public string Key => MetadataProvider.MusicBrainzAlbumArtist.ToString();

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.AlbumArtist;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Audio;
}
