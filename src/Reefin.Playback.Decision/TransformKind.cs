namespace Reefin.Playback.Decision;

/// <summary>
/// A single transformation the playback pipeline must perform to realize a <see cref="PlaybackDecision"/>.
/// Orthogonal to <see cref="PlaybackMethod"/>: the method says which delivery strategy was chosen,
/// the transforms say what work that strategy actually requires of the pipeline.
/// </summary>
public enum TransformKind
{
    /// <summary>
    /// Remux the source container into a different output container; streams are copied, not re-encoded.
    /// </summary>
    RemuxContainer,

    /// <summary>
    /// Re-encode the video stream to a different codec, resolution, or video range.
    /// </summary>
    TranscodeVideo,

    /// <summary>
    /// Re-encode the audio stream to a different codec or channel layout.
    /// </summary>
    TranscodeAudio,

    /// <summary>
    /// Copy the video stream unmodified.
    /// </summary>
    CopyVideo,

    /// <summary>
    /// Copy the audio stream unmodified.
    /// </summary>
    CopyAudio,

    /// <summary>
    /// Reduce the number of audio channels (for example, 5.1 down to stereo).
    /// </summary>
    Downmix,

    /// <summary>
    /// Convert HDR video to SDR.
    /// </summary>
    Tonemap,

    /// <summary>
    /// Burn a subtitle track into the video image.
    /// </summary>
    BurnInSubtitle,

    /// <summary>
    /// Extract a subtitle track for external or embedded delivery.
    /// </summary>
    ExtractSubtitle,
}
