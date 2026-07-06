using System.Collections.Generic;
using Reefin.Common.Configuration;
using Reefin.Model.Branding;

namespace Reefin.Server.Core.Branding
{
    /// <summary>
    /// A configuration factory for <see cref="BrandingOptions"/>.
    /// </summary>
    public class BrandingConfigurationFactory : IConfigurationFactory
    {
        /// <inheritdoc />
        public IEnumerable<ConfigurationStore> GetConfigurations()
        {
            return new[]
            {
                new ConfigurationStore
                {
                     ConfigurationType = typeof(BrandingOptions),
                     Key = "branding"
                }
            };
        }
    }
}
