using System.Collections.Generic;
using Tesserafin.Database.Implementations.Entities.Libraries;

namespace Tesserafin.Database.Implementations.Interfaces;

/// <summary>
/// An abstraction representing an entity that has releases.
/// </summary>
public interface IHasReleases
{
    /// <summary>
    /// Gets a collection containing this entity's releases.
    /// </summary>
    ICollection<Release> Releases { get; }
}
