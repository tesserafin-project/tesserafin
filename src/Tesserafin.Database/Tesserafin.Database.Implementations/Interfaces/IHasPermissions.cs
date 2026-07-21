using System.Collections.Generic;
using Tesserafin.Database.Implementations.Entities;

namespace Tesserafin.Database.Implementations.Interfaces;

/// <summary>
/// An abstraction representing an entity that has permissions.
/// </summary>
public interface IHasPermissions
{
    /// <summary>
    /// Gets a collection containing this entity's permissions.
    /// </summary>
    ICollection<Permission> Permissions { get; }
}
