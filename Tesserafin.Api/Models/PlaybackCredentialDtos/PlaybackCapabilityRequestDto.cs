using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Api.Models.PlaybackCredentialDtos;

/// <summary>
/// Asks the server to mint a playback capability.
/// </summary>
/// <remarks>
/// POST with a body, never a query string: a request that names an item and a play session in its
/// URL writes both into every proxy log between the client and the server, and the point of this
/// endpoint is to stop putting things in URLs that do not need to be there.
/// </remarks>
public class PlaybackCapabilityRequestDto
{
    /// <summary>
    /// Gets or sets the play session this capability belongs to. Ending that play session revokes
    /// this capability and no other.
    /// </summary>
    [Required]
    public string PlaySessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item whose media this capability may fetch. Omitted only for a scope that
    /// is not item-bound, which today means <see cref="PlaybackCapabilityScope.Fonts"/>.
    /// </summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the media source within that item.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the kinds of media this capability may fetch. Must name at least one; a
    /// capability with no scope would grant nothing.
    /// </summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<PlaybackCapabilityScope> Scopes { get; set; } = Array.Empty<PlaybackCapabilityScope>();
}
