using System.Collections.Generic;
using Tesserafin.Database.Implementations.Entities.Libraries;

namespace Tesserafin.Database.Implementations.Interfaces
{
    /// <summary>
    /// An abstraction representing an entity that has companies.
    /// </summary>
    public interface IHasCompanies
    {
        /// <summary>
        /// Gets a collection containing this entity's companies.
        /// </summary>
        ICollection<Company> Companies { get; }
    }
}
