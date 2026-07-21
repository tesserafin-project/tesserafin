#pragma warning disable CS1591

using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.TV
{
    public class Zap2ItExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => "Zap2It";

        /// <inheritdoc />
        public string Key => MetadataProvider.Zap2It.ToString();

        /// <inheritdoc />
        public ExternalIdMediaType? Type => null;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Series;
    }
}
