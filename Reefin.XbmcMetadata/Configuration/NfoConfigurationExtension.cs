#pragma warning disable CS1591

using Reefin.Common.Configuration;
using Reefin.Model.Configuration;

namespace Reefin.XbmcMetadata.Configuration
{
    public static class NfoConfigurationExtension
    {
        public static XbmcMetadataOptions GetNfoConfiguration(this IConfigurationManager manager)
        {
            return manager.GetConfiguration<XbmcMetadataOptions>("xbmcmetadata");
        }
    }
}
