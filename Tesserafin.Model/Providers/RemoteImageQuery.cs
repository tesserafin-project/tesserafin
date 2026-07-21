#pragma warning disable CS1591

using Tesserafin.Model.Entities;

namespace Tesserafin.Model.Providers
{
    public class RemoteImageQuery
    {
        public RemoteImageQuery(string providerName)
        {
            ProviderName = providerName;
        }

        public string ProviderName { get; }

        public ImageType? ImageType { get; set; }

        public bool IncludeDisabledProviders { get; set; }

        public bool IncludeAllLanguages { get; set; }
    }
}
