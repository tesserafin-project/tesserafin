using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Plugins.MusicBrainz;

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
