using System;

namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// Opaque identifier for a <see cref="PlaybackSession"/>.
/// </summary>
/// <param name="Value">The underlying identifier value.</param>
public readonly record struct PlaybackSessionId(Guid Value)
{
    /// <summary>
    /// Creates a new, unique <see cref="PlaybackSessionId"/>.
    /// </summary>
    /// <returns>The new identifier.</returns>
    public static PlaybackSessionId NewId() => new(Guid.NewGuid());

    /// <inheritdoc/>
    public override string ToString() => Value.ToString("N");
}
