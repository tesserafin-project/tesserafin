using System;
using System.Collections.Generic;

namespace Reefin.Playback.Decision;

/// <summary>
/// Whether one text subtitle format can be re-encoded into another, mirroring legacy
/// <c>Reefin.Model.Entities.MediaStream.SupportsSubtitleConversionTo</c> (MediaStream.cs:772): any
/// text subtitle format converts to any other, except that <c>ass</c>/<c>ssa</c> can neither be
/// converted from nor converted to. Deliberately has no <c>srt</c>/<c>vtt</c> literals of its own -
/// extensible by construction to any text format pair the caller already knows are both text-based.
/// </summary>
public static class SubtitleTextConversion
{
    private static readonly IReadOnlySet<string> NoConversion =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ass", "ssa" };

    /// <summary>
    /// Whether a text subtitle in <paramref name="fromFormat"/> can be converted to <paramref name="toFormat"/>.
    /// </summary>
    /// <param name="fromFormat">The source text subtitle format.</param>
    /// <param name="toFormat">The candidate target text subtitle format.</param>
    /// <returns><see langword="true"/> unless either format is <c>ass</c> or <c>ssa</c>.</returns>
    public static bool CanConvert(string fromFormat, string toFormat) =>
        !NoConversion.Contains(fromFormat) && !NoConversion.Contains(toFormat);
}
