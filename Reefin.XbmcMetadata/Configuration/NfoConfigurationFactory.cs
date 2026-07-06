#pragma warning disable CS1591

using System.Collections.Generic;
using MediaBrowser.Model.Configuration;
using Reefin.Common.Configuration;

namespace Reefin.XbmcMetadata.Configuration
{
    public class NfoConfigurationFactory : IConfigurationFactory
    {
        /// <inheritdoc />
        public IEnumerable<ConfigurationStore> GetConfigurations()
        {
            return new[]
            {
                new ConfigurationStore
                {
                    ConfigurationType = typeof(XbmcMetadataOptions),
                    Key = "xbmcmetadata"
                }
            };
        }
    }
}
