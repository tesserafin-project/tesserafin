using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Plugins.MusicBrainz;

/// <summary>
/// MusicBrainz recording id.
/// </summary>
public class MusicBrainzRecordingId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MusicBrainz";

    /// <inheritdoc />
    public string Key => MetadataProvider.MusicBrainzRecording.ToString();

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Recording;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Audio;
}
