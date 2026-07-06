#pragma warning disable CS1591

using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

namespace Reefin.Providers.Plugins.AudioDb
{
    public class AudioDbOtherAlbumExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "TheAudioDb";

        /// <inheritdoc />
        public string Key => MetadataProvider.AudioDbAlbum.ToString();

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Album;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Audio;
    }
}
