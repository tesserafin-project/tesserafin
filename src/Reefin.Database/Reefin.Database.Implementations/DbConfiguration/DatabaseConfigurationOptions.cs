using System.Collections.Generic;

namespace Reefin.Database.Implementations.DbConfiguration;

/// <summary>
/// Options to configure reefins managed database.
/// </summary>
public class DatabaseConfigurationOptions
{
    /// <summary>
    /// Gets or Sets the type of database reefin should use.
    /// </summary>
    public required string DatabaseType { get; set; }

    /// <summary>
    /// Gets or sets the options required to use a custom database provider.
    /// </summary>
    public CustomDatabaseOptions? CustomProviderOptions { get; set; }

    /// <summary>
    /// Gets or Sets the kind of locking behavior reefin should perform. Possible options are "NoLock", "Pessimistic", "Optimistic".
    /// Defaults to "NoLock".
    /// </summary>
    public DatabaseLockingBehaviorTypes LockingBehavior { get; set; }
}
