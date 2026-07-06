#pragma warning disable CS1591

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Reefin.Providers.Music
{
    public class ImvdbId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "IMVDb";

        /// <inheritdoc />
        public string Key => "IMVDb";

        /// <inheritdoc />
        public ExternalIdMediaType? Type => null;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item)
            => item is MusicVideo;
    }
}
