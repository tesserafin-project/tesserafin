using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Tesserafin.Api.Models.ContentPackDtos;

/// <summary>
/// The body of a reorder content packs request.
/// </summary>
public class ReorderContentPacksRequest
{
    /// <summary>
    /// Gets or sets every content pack id, exactly once, in the wanted order.
    /// </summary>
    /// <remarks>
    /// A partial list is rejected: the whole ordering is replaced in one transaction so the result
    /// is always contiguous and complete.
    /// </remarks>
    [Required]
    public required IReadOnlyList<Guid> PackIds { get; set; }
}
