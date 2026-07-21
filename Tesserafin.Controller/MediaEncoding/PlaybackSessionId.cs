using System;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Opaque identifier for a <see cref="PlaybackSession"/>.
/// </summary>
/// <param name="Value">The underlying identifier value.</param>
public readonly record struct PlaybackSessionId(Guid Value) : IParsable<PlaybackSessionId>
{
    /// <summary>
    /// Creates a new, unique <see cref="PlaybackSessionId"/>.
    /// </summary>
    /// <returns>The new identifier.</returns>
    public static PlaybackSessionId NewId() => new(Guid.NewGuid());

    /// <summary>
    /// Parses a <see cref="PlaybackSessionId"/> from its string form. Lets ASP.NET Core bind it
    /// directly from route/query values.
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="provider">Ignored — the underlying <see cref="Guid"/> parse is culture-invariant.</param>
    /// <returns>The parsed identifier.</returns>
    public static PlaybackSessionId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    /// <inheritdoc cref="Parse(string, IFormatProvider)"/>
    public static bool TryParse(string? s, IFormatProvider? provider, out PlaybackSessionId result)
    {
        if (Guid.TryParse(s, out var value))
        {
            result = new PlaybackSessionId(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => Value.ToString("N");
}
