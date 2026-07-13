namespace Reefin.Playback.Decision;

/// <summary>
/// A subtitle format a client can render, and how it wants it delivered.
/// </summary>
/// <param name="Format">The normalized subtitle format (for example <c>"srt"</c>, <c>"pgs"</c>).</param>
/// <param name="Method">The delivery method the client supports for this format.</param>
public sealed record SubtitleCapability(string Format, SubtitleDeliveryMethod Method);
