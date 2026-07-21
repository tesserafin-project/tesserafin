using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Enums;

namespace Tesserafin.Controller.Sorting
{
    /// <summary>
    /// Interface IBaseItemComparer.
    /// </summary>
    public interface IBaseItemComparer : IComparer<BaseItem?>
    {
        /// <summary>
        /// Gets the comparer type.
        /// </summary>
        ItemSortBy Type { get; }
    }
}
