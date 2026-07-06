#pragma warning disable CS1591

using Reefin.Controller.Entities;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.Providers;

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
