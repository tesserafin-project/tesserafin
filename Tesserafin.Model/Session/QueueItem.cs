#nullable disable
#pragma warning disable CS1591

using System;

namespace Tesserafin.Model.Session;

public record QueueItem
{
    public Guid Id { get; set; }

    public string PlaylistItemId { get; set; }
}
