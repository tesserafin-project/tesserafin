using System;
using System.Collections.Generic;
using Tesserafin.Common.Configuration;

namespace Tesserafin.Server.Implementations.DatabaseConfiguration;

/// <summary>
/// Factory for constructing a database configuration.
/// </summary>
public class DatabaseConfigurationFactory : IConfigurationFactory
{
    /// <inheritdoc/>
    public IEnumerable<ConfigurationStore> GetConfigurations()
    {
        yield return new DatabaseConfigurationStore();
    }
}
