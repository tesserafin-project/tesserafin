using Reefin.Model.SyncPlay;

namespace Reefin.Api.Models.SyncPlayDtos;

/// <summary>
/// Class SetShuffleModeRequestDto.
/// </summary>
public class SetShuffleModeRequestDto
{
    /// <summary>
    /// Gets or sets the shuffle mode.
    /// </summary>
    /// <value>The shuffle mode.</value>
    public GroupShuffleMode Mode { get; set; }
}
