using System;
using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Model.Dto;

namespace Reefin.Controller.Library;

/// <summary>
/// A recommendation category derived from a baseline item, holding similar items prior to DTO conversion.
/// </summary>
public sealed class SimilarItemsRecommendation
{
    /// <summary>
    /// Gets the display name of the baseline item the recommendation is based on.
    /// </summary>
    public required string BaselineItemName { get; init; }

    /// <summary>
    /// Gets an identifier for the recommendation category.
    /// </summary>
    public required Guid CategoryId { get; init; }

    /// <summary>
    /// Gets the recommendation type.
    /// </summary>
    public required RecommendationType RecommendationType { get; init; }

    /// <summary>
    /// Gets the similar items for the baseline, ordered by relevance.
    /// </summary>
    public required IReadOnlyList<BaseItem> Items { get; init; }
}
