namespace Reefin.Playback.Decision;

/// <summary>
/// A frozen snapshot of one subtitle stream on a <see cref="MediaSourceSnapshot"/>, decoupled from
/// any probing/model type.
/// </summary>
/// <param name="Index">The stream index within the source.</param>
/// <param name="Format">The normalized subtitle format (for example <c>"srt"</c>, <c>"pgs"</c>).</param>
/// <param name="IsExternal">Whether the subtitle track is stored externally to the source.</param>
/// <param name="IsForced">Whether the subtitle track is marked forced.</param>
/// <param name="IsDefault">Whether this is the default subtitle stream on the source.</param>
/// <param name="Language">The stream's language tag, or <see langword="null"/> if unknown.</param>
public sealed record SubtitleStreamSnapshot(
    int Index,
    string Format,
    bool IsExternal,
    bool IsForced,
    bool IsDefault,
    string? Language);
