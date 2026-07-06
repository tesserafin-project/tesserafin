#pragma warning disable CS1591

using System.Collections.Generic;
using Reefin.Common.Configuration;
using Reefin.Model.Configuration;

namespace Reefin.Controller.Library
{
    public class MetadataConfigurationStore : IConfigurationFactory
    {
        public IEnumerable<ConfigurationStore> GetConfigurations()
        {
            return new ConfigurationStore[]
            {
                new ConfigurationStore
                {
                    Key = "metadata",
                    ConfigurationType = typeof(MetadataConfiguration)
                }
            };
        }
    }
}
