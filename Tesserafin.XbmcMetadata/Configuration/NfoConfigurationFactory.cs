#pragma warning disable CS1591

using System.Collections.Generic;
using Tesserafin.Common.Configuration;
using Tesserafin.Model.Configuration;

namespace Tesserafin.XbmcMetadata.Configuration
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
