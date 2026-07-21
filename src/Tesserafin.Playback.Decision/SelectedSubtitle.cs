namespace Tesserafin.Playback.Decision;

/// <summary>
/// The subtitle stream selected for a <see cref="PlaybackDecision"/>, and how it will be delivered.
/// </summary>
/// <param name="Index">The selected subtitle stream index.</param>
/// <param name="Delivery">How the subtitle will be delivered to the client.</param>
public sealed record SelectedSubtitle(int Index, SubtitleDeliveryMethod Delivery);
