using System.Collections.Generic;
using Reefin.Common.Configuration;
using Reefin.Model.LiveTv;

namespace Reefin.LiveTv.Configuration;

/// <summary>
/// <see cref="IConfigurationFactory" /> implementation for <see cref="LiveTvOptions" />.
/// </summary>
public class LiveTvConfigurationFactory : IConfigurationFactory
{
    /// <inheritdoc />
    public IEnumerable<ConfigurationStore> GetConfigurations()
    {
        return new[]
        {
            new ConfigurationStore
            {
                ConfigurationType = typeof(LiveTvOptions),
                Key = "livetv"
            }
        };
    }
}
