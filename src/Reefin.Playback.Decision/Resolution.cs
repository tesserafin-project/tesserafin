namespace Reefin.Playback.Decision;

/// <summary>
/// A pixel width/height pair.
/// </summary>
/// <param name="Width">The width, in pixels.</param>
/// <param name="Height">The height, in pixels.</param>
public sealed record Resolution(int Width, int Height);
