#pragma warning disable CS1591

using System.Collections.Generic;
using Tesserafin.Common.Configuration;

namespace Tesserafin.MediaEncoding.Configuration
{
    public class EncodingConfigurationFactory : IConfigurationFactory
    {
        public IEnumerable<ConfigurationStore> GetConfigurations()
        {
            return new[]
            {
                new EncodingConfigurationStore()
            };
        }
    }
}
