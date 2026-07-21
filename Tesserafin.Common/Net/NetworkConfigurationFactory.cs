using System.Collections.Generic;
using Tesserafin.Common.Configuration;

namespace Tesserafin.Common.Net;

/// <summary>
/// Defines the <see cref="NetworkConfigurationFactory" />.
/// </summary>
public class NetworkConfigurationFactory : IConfigurationFactory
{
    /// <summary>
    /// The GetConfigurations.
    /// </summary>
    /// <returns>The <see cref="IEnumerable{ConfigurationStore}"/>.</returns>
    public IEnumerable<ConfigurationStore> GetConfigurations()
    {
        return new[]
        {
            new NetworkConfigurationStore()
        };
    }
}
