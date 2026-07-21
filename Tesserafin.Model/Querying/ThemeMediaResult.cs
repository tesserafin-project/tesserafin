using System;
using Tesserafin.Model.Dto;

namespace Tesserafin.Model.Querying
{
    /// <summary>
    /// Class ThemeMediaResult.
    /// </summary>
    public class ThemeMediaResult : QueryResult<BaseItemDto>
    {
        /// <summary>
        /// Gets or sets the owner id.
        /// </summary>
        /// <value>The owner id.</value>
        public Guid OwnerId { get; set; }
    }
}
