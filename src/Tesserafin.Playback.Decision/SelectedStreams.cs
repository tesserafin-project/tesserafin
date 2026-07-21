namespace Tesserafin.Playback.Decision;

/// <summary>
/// The video, audio, and subtitle streams selected for a <see cref="PlaybackDecision"/>.
/// </summary>
/// <param name="Video">The selected video stream index, or <see langword="null"/> if no video stream was selected.</param>
/// <param name="Audio">The selected audio stream index, or <see langword="null"/> if no audio stream was selected.</param>
/// <param name="Subtitle">The selected subtitle stream and delivery method, or <see langword="null"/> if no subtitle was selected.</param>
public sealed record SelectedStreams(int? Video, int? Audio, SelectedSubtitle? Subtitle)
{
    /// <summary>
    /// No streams selected: no video, no audio, no subtitle.
    /// </summary>
    public static readonly SelectedStreams None = new(null, null, null);
}
