#pragma warning disable CS1591

using MediaBrowser.Model.Configuration;
using Reefin.Common.Configuration;

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
