#pragma warning disable CS1591

using Tesserafin.Common.Configuration;
using Tesserafin.Model.Configuration;

namespace Tesserafin.XbmcMetadata.Configuration
{
    public static class NfoConfigurationExtension
    {
        public static XbmcMetadataOptions GetNfoConfiguration(this IConfigurationManager manager)
        {
            return manager.GetConfiguration<XbmcMetadataOptions>("xbmcmetadata");
        }
    }
}
