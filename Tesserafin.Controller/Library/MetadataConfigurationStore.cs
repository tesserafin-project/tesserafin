#pragma warning disable CS1591

using System.Collections.Generic;
using Tesserafin.Common.Configuration;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.Library
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
