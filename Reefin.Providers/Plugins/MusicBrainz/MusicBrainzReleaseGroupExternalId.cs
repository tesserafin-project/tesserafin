using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.MusicBrainz;

/// <summary>
/// MusicBrainz release group external id.
/// </summary>
public class MusicBrainzReleaseGroupExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MusicBrainz";

    /// <inheritdoc />
    public string Key => MetadataProvider.MusicBrainzReleaseGroup.ToString();

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.ReleaseGroup;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Audio or MusicAlbum;
}
